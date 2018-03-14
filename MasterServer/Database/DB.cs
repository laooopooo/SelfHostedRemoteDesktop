﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BPUtil;
using SHRDLib;
using SQLite;

namespace MasterServer.Database
{
	/// <summary>
	/// This is meant to be accessed via ServiceWrapper.db.
	/// </summary>
	public class DB : IDisposable
	{
		private static object syncLock = new object();
		SQLiteConnection db = null;
		/// <summary>
		/// This is meant to be accessed via ServiceWrapper.db.
		/// </summary>
		public DB()
		{
			lock (syncLock)
			{
				FileInfo fiDB = new FileInfo("SHRDdb.s3db");
				fiDB.Delete(); // TODO: Remove this line!

				db = new SQLiteConnection(fiDB.FullName);

				db.CreateTable<AbstractSetting>();
				db.CreateTable<Computer>();
				db.CreateTable<ComputerGroupMembership>();
				db.CreateTable<User>();
				db.CreateTable<UserGroup>();
				db.CreateTable<UserGroupMembership>();

				// Pre-populate database with admin user only if no other users exist.
				int userCount = db.CreateCommand("SELECT COUNT (*) FROM [User]").ExecuteScalar<int>();
				if (userCount == 0)
				{
					User admin = new User("admin", "admin", "Administrator", "admin@example.com", true);
					admin.Permanent = true;
					db.Insert(admin);
					//List<AbstractSetting> newSettings = new List<AbstractSetting>();
					//newSettings.Add(new AbstractSetting(SettingsKey.mayAccessComputers, true));
					//newSettings.Add(new AbstractSetting(SettingsKey.mayEditComputers, true));
					//newSettings.Add(new AbstractSetting(SettingsKey.mayViewGroups, true));
					//newSettings.Add(new AbstractSetting(SettingsKey.mayEditGroups, true));
					//newSettings.Add(new AbstractSetting(SettingsKey.mayViewUsers, true));
					//newSettings.Add(new AbstractSetting(SettingsKey.mayEditUsers, true));
					//newSettings.Add(new AbstractSetting(SettingsKey.mayViewStatusPage, true));
					//AddOrUpdateSettings(ForeignKeyType.User, admin.ID, newSettings);
				}

				// Pre-populate database with two groups and three computers in each group.
				int groupCount = db.CreateCommand("SELECT COUNT (*) FROM [UserGroup]").ExecuteScalar<int>();
				if (groupCount == 0)
				{
					UserGroup group_uncat = new UserGroup("Uncategorized");
					group_uncat.Permanent = true;
					db.Insert(group_uncat);

					//for (int a = 1; a <= 2; a++)
					//{
					//	UserGroup group = new UserGroup("Group " + a);
					//	db.Insert(group);
					//	for (int b = 1; b <= 3; b++)
					//	{
					//		Computer comp = new Computer();
					//		comp.Name = "Computer " + a + "-" + b;
					//		db.Insert(comp);
					//		db.Insert(new ComputerGroupMembership(group.ID, comp.ID));
					//	}
					//}
				}

				//// Pre-populate database with Administrators group only if no other groups exist.
				//int? administratorsGroupId = null;
				//int groupCount = db.CreateCommand("SELECT COUNT (*) FROM [UserGroup]").ExecuteScalar<int>();
				//if (groupCount == 0)
				//	administratorsGroupId = db.Insert(new UserGroup("Administrators"));

				//if (adminUserId != null && administratorsGroupId != null)
				//{
				//	if (db.Table<UserGroupMembership>().Where(m => m.GroupID == administratorsGroupId.Value && m.UserID == adminUserId.Value).Count() == 0)
				//	{
				//		db.Insert(new UserGroupMembership(administratorsGroupId.Value, adminUserId.Value));
				//	}
				//}
			}
		}

		#region IDisposable Support
		private bool disposedValue = false; // To detect redundant calls

		protected virtual void Dispose(bool disposing)
		{
			if (disposing)
			{
				db?.Close();
				db?.Dispose();
				db = null;
			}
			if (!disposedValue)
			{

				// TODO: free unmanaged resources (unmanaged objects) and override a finalizer below.
				// TODO: set large fields to null.

				disposedValue = true;
			}
		}

		// TODO: override a finalizer only if Dispose(bool disposing) above has code to free unmanaged resources.
		// ~DB() {
		//   // Do not change this code. Put cleanup code in Dispose(bool disposing) above.
		//   Dispose(false);
		// }

		// This code added to correctly implement the disposable pattern.
		public void Dispose()
		{
			// Do not change this code. Put cleanup code in Dispose(bool disposing) above.
			Dispose(true);
			// TODO: uncomment the following line if the finalizer is overridden above.
			// GC.SuppressFinalize(this);
		}
		#endregion

		/// <summary>
		/// Returns the user with the specified name, or null.
		/// </summary>
		/// <param name="userName">The user name (case insensitive).</param>
		/// <returns></returns>
		public User GetUser(string name)
		{
			return db.Query<User>("SELECT * FROM User WHERE Name = ?", name).FirstOrDefault();
		}
		/// <summary>
		/// Returns the user with the specified ID, or null.
		/// </summary>
		/// <param name="userId"></param>
		/// <returns></returns>
		public User GetUser(int userId)
		{
			return db.Query<User>("SELECT * FROM User WHERE ID = ?", userId).FirstOrDefault();
		}
		public UserGroup[] GetUserGroups(int userId)
		{
			HashSet<int> groupIds = new HashSet<int>(GetUserGroupMemberships(userId).Select(m => m.GroupID));
			return db.Table<UserGroup>().Where(g => groupIds.Contains(g.ID)).ToArray();
		}
		public UserGroup[] GetAllGroups()
		{
			return db.Table<UserGroup>().ToArray();
		}
		/// <summary>
		/// Gets a list of all computers and the list of groups that each computer is associated with.
		/// </summary>
		/// <returns></returns>
		public ComputerAndItsGroups[] GetAllComputersAndTheirGroups()
		{
			UserGroup[] allGroups = null;
			Computer[] allComputers = null;
			ComputerGroupMembership[] allMemberships = null;

			/// Get all computer and group and membership data in a thread-safe manner.
			db.RunInTransaction(() =>
			{
				allGroups = db.Table<UserGroup>().ToArray();
				allComputers = db.Table<Computer>().ToArray();
				allMemberships = db.Table<ComputerGroupMembership>().ToArray();
			});

			// Organize group and computer information for efficient access.
			Dictionary<int, UserGroup> groupMap = new Dictionary<int, UserGroup>();
			foreach (UserGroup g in allGroups)
				groupMap[g.ID] = g;
			Dictionary<int, ComputerAndItsGroups> computerMap = new Dictionary<int, ComputerAndItsGroups>();
			foreach (Computer c in allComputers)
				computerMap[c.ID] = new ComputerAndItsGroups() { Computer = c };

			// Associate computers and their groups
			foreach (ComputerGroupMembership m in allMemberships)
			{
				Try.Catch_RethrowThreadAbort(() =>
				{
					ComputerAndItsGroups c = computerMap[m.ComputerID];
					UserGroup g = groupMap[m.GroupID];
					c.Groups.Add(g);
				});
			}
			return computerMap.Values.ToArray();
		}
		/// <summary>
		/// Gets a list of all users and the list of groups that each user is associated with.
		/// </summary>
		/// <returns></returns>
		public UserAndItsGroups[] GetAllUsersAndTheirGroups()
		{
			UserGroup[] allGroups = null;
			User[] allUsers = null;
			UserGroupMembership[] allMemberships = null;

			/// Get all user and group and membership data in a thread-safe manner.
			db.RunInTransaction(() =>
			{
				allGroups = db.Table<UserGroup>().ToArray();
				allUsers = db.Table<User>().ToArray();
				allMemberships = db.Table<UserGroupMembership>().ToArray();
			});

			// Organize group and user information for efficient access.
			Dictionary<int, UserGroup> groupMap = new Dictionary<int, UserGroup>();
			foreach (UserGroup g in allGroups)
				groupMap[g.ID] = g;
			Dictionary<int, UserAndItsGroups> userMap = new Dictionary<int, UserAndItsGroups>();
			foreach (User u in allUsers)
				userMap[u.ID] = new UserAndItsGroups() { User = u };

			// Associate users and their groups
			foreach (UserGroupMembership m in allMemberships)
			{
				Try.Catch_RethrowThreadAbort(() =>
				{
					UserAndItsGroups u = userMap[m.UserID];
					UserGroup g = groupMap[m.GroupID];
					u.Groups.Add(g);
				});
			}
			return userMap.Values.ToArray();
		}

		/// <summary>
		/// Returns an array of group memberships that the specified user ID is part of.
		/// </summary>
		/// <param name="userId">The user ID to get memberships for.</param>
		/// <returns></returns>
		public UserGroupMembership[] GetUserGroupMemberships(int userId)
		{
			return db.Query<UserGroupMembership>("SELECT * FROM UserGroupMembership WHERE UserID = ?", userId).ToArray();
		}
		/// <summary>
		/// Returns an array of group memberships that the specified computer ID is part of.
		/// </summary>
		/// <param name="computerId">The computer ID to get memberships for.</param>
		/// <returns></returns>
		public ComputerGroupMembership[] GetComputerGroupMemberships(int computerId)
		{
			return db.Query<ComputerGroupMembership>("SELECT * FROM ComputerGroupMembership WHERE ComputerID = ?", computerId).ToArray();
		}
		public UserGroup[] GetComputerGroups(int computerId)
		{
			HashSet<int> groupIds = new HashSet<int>(GetComputerGroupMemberships(computerId).Select(m => m.GroupID));
			return db.Table<UserGroup>().Where(g => groupIds.Contains(g.ID)).ToArray();
		}
		/// <summary>
		/// Gets the computer with the specified name, or null.
		/// </summary>
		/// <param name="computerName"></param>
		/// <returns></returns>
		public Computer GetComputer(string name)
		{
			return db.Query<Computer>("SELECT * FROM Computer WHERE Name = ?", name).FirstOrDefault();
		}
		/// <summary>
		/// Gets the computer with the specified ID, or null.
		/// </summary>
		/// <param name="computerName"></param>
		/// <returns></returns>
		public Computer GetComputer(int computerId)
		{
			return db.Query<Computer>("SELECT * FROM Computer WHERE ID = ?", computerId).FirstOrDefault();
		}
		/// <summary>
		/// Adds a computer. Because computers are added automatically during the initial connection of a new Host, this method may change the provided name without notice if required to ensure uniqueness.
		/// If unsuccessful, an exception is thrown.
		/// </summary>
		/// <param name="computer"></param>
		/// <returns></returns>
		public void AddComputer(Computer computer)
		{
			string originalName = computer.Name;
			db.RunInTransaction(() =>
			{
				bool gotUniqueName = Util.AttemptUntilTrue(10, 50, 400, (attemptNumber) =>
				{
					if (attemptNumber > 1)
						computer.Name = Util.MakeNameUnique(computer.Name, (uint)attemptNumber, 128);
					Computer existing = GetComputer(computer.Name);
					return existing == null;
				});
				if (!gotUniqueName)
					computer.Name = "SHRD-COMP-" + Guid.NewGuid() + "-" + originalName;
				db.Insert(computer);
			});
		}
		/// <summary>
		/// Adds a user.  If unsuccessful (such as if there is a name collision), an exception is thrown.
		/// </summary>
		/// <param name="user"></param>
		/// <returns></returns>
		public void AddUser(User user)
		{
			db.Insert(user);
		}
		#region Abstract Settings
		public AbstractSetting[] GetGroupSettings(int GroupID)
		{
			return GetSettings(ForeignKeyType.UserGroup, GroupID);
		}
		public AbstractSetting[] GetUserSettings(int UserID)
		{
			return GetSettings(ForeignKeyType.User, UserID);
		}
		private AbstractSetting[] GetSettings(ForeignKeyType type, int foreignKey)
		{
			return db.Table<AbstractSetting>().Where(s => s.ForeignKeyType == type && s.ForeignKey == foreignKey).ToArray();
		}
		/// <summary>
		/// Adds or updates the specified settings.
		/// </summary>
		/// <param name="type">The type of object these settings are associated with.</param>
		/// <param name="foreignKey">The ID of the object these settings are associated with.</param>
		/// <param name="settings">A collection of AbstractSetting instances to add or update. You need only set the SettingID and Value properties in each AbstractSetting instance.</param>
		private void AddOrUpdateSettings(ForeignKeyType type, int foreignKey, IEnumerable<AbstractSetting> settings)
		{
			db.RunInTransaction(() =>
			{
				AbstractSetting[] existingSettings = GetSettings(type, foreignKey);
				foreach (AbstractSetting newSetting in settings)
				{
					AbstractSetting existingSetting = existingSettings.FirstOrDefault(s => s.SettingsID == newSetting.SettingsID);
					if (existingSetting != null)
					{
						existingSetting.Value = newSetting.Value;
						db.Update(existingSetting);
					}
					else
					{
						newSetting.ForeignKeyType = type;
						newSetting.ForeignKey = foreignKey;
						db.Insert(newSetting);
					}
				}
			});
		}
		#endregion
	}
}
