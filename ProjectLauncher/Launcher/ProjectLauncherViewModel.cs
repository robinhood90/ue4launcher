﻿using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Timers;
using System.Windows;
using System.Windows.Media;
using System.Xml.Serialization;
using UE4Launcher.Debugging;
using UE4Launcher.Root;

namespace UE4Launcher.Launcher
{
	internal class ProjectLauncherViewModel : PageViewModelBase, IDebuggerSupportedViewModel
	{
		private string _publicProfileStorage;
		private string _personalProfileStorage;

		private LaunchProfileViewModel _selectedProfile;
		public bool ShowProfileNotSavingTip => !this.EditMode;

		public ObservableCollection<LaunchProfileViewModel> Profiles { get; }

		public LaunchProfileViewModel SelectedProfile
		{
			get => _selectedProfile;
			set
			{
				if (_selectedProfile == value)
					return;

				var oldValue = _selectedProfile;
				_selectedProfile = value;
				this.RaisePropertyChanged(nameof(this.SelectedProfile));
				this.OnSelectedProfileChanged(oldValue, _selectedProfile);
			}
		}

		public bool AttachDebugger
		{
			get => Preferences.Default.AttachDebugger;
			set
			{
				Preferences.Default.AttachDebugger = value;
				Preferences.Default.Save();

				if (value)
				{
					this.RefreshDebuggers();
				}

				this.RaisePropertyChanged(nameof(this.AttachDebugger));
			}
		}

		public ObservableCollection<IDebuggerInfo> Debuggers { get; } = new ObservableCollection<IDebuggerInfo>();
		ICollection<IDebuggerInfo> IDebuggerSupportedViewModel.Debuggers => this.Debuggers;

		private IDebuggerInfo _selectedDebugger;

		public IDebuggerInfo SelectedDebugger
		{
			get => _selectedDebugger;
			set
			{
				_selectedDebugger = value;
				this.RaisePropertyChanged(nameof(this.SelectedDebugger));
			}
		}

		public ProjectLauncherViewModel(MainWindowViewModel owner)
			: base(owner)
		{
			_ping = this.InitializePing(out _pingTimer);
			this.Profiles = new ObservableCollection<LaunchProfileViewModel>();
			this.RefreshDebuggers();
			this.LoadProfiles();
		}

		private void OnSelectedProfileChanged(LaunchProfileViewModel oldProfile, LaunchProfileViewModel newProfile)
		{
			this.RaisePropertyChanged(nameof(this.ReleaseVersion));
			this.RaisePropertyChanged(nameof(this.ServerPingBrush));
			this.RaisePropertyChanged(nameof(this.ShowServerPing));
			this.RaisePropertyChanged(nameof(this.ServerPingTitleText));
			this.ServerPing = "<pinging...>";

			if (oldProfile != null)
			{
				newProfile.UrlChanged -= this.Profile_UrlChanged;
				newProfile.SelectedExecutableFileChanged -= this.Profile_SelectedExecutableFileChanged;
				newProfile.OpenModeChanged -= this.Profile_OpenModeChanged;
			}

			if (newProfile != null)
			{
				newProfile.UrlChanged += this.Profile_UrlChanged;
				newProfile.SelectedExecutableFileChanged += this.Profile_SelectedExecutableFileChanged;
				newProfile.OpenModeChanged += this.Profile_OpenModeChanged;
			}
		}

		private void Profile_OpenModeChanged(object sender, EventArgs e)
		{
			this.RaisePropertyChanged(nameof(this.ShowServerPing));
		}

		private void Profile_SelectedExecutableFileChanged(object sender, EventArgs e)
		{
			this.RaisePropertyChanged(nameof(this.ReleaseVersion));
		}

		private void Profile_UrlChanged(object sender, EventArgs e)
		{
			this.RaisePropertyChanged(nameof(this.ServerPingBrush));
			this.RaisePropertyChanged(nameof(this.ShowServerPing));
			this.RaisePropertyChanged(nameof(this.ServerPingTitleText));
			this.ServerPing = "<pinging...>";
		}

		public string ReleaseVersion
		{
			get
			{
				if (this.SelectedProfile == null)
					return "<unknown>";

				var executableFilePath = this.SelectedProfile.SelectedExecutableFile.Path;
				Debug.Assert(executableFilePath != null, "executableFilePath != null");

				var versionFile = Path.ChangeExtension(executableFilePath, ".version");
				if (File.Exists(versionFile))
				{
					Debug.Assert(versionFile != null, "versionFile != null");
					return File.ReadAllText(versionFile);
				}

				if (!File.Exists(executableFilePath))
				{
					return "<file not existed>";
				}

				var fileInfo = new FileInfo(executableFilePath);
				return fileInfo.LastWriteTimeUtc.ToString(CultureInfo.InvariantCulture);
			}
		}



		private readonly Ping _ping;
		private readonly Timer _pingTimer;


		private long _serverPingValue;
		private string _serverPing;

		public string ServerPing
		{
			get => _serverPing;
			private set
			{
				_serverPing = value;
				this.RaisePropertyChanged(nameof(this.ServerPing));
				this.RaisePropertyChanged(nameof(this.ServerPingBrush));
			}
		}

		public string ServerPingTitleText => $"Ping to {this.SelectedProfile?.Profile.Url}: ";

		public Brush ServerPingBrush
		{
			get
			{
				if (_serverPingValue < 50)
					return Brushes.Green;

				if (_serverPingValue < 100)
					return Brushes.Orange;

				return Brushes.Red;
			}
		}

		public bool ShowServerPing => this.SelectedProfile?.ConnectToServer ?? false;


		private Ping InitializePing(out Timer pingTimer)
		{
			var ping = new Ping();
			ping.PingCompleted += this.Ping_PingCompleted;
			pingTimer = new Timer(1000) { AutoReset = false };
			pingTimer.Elapsed += this.PingTimer_Elapsed;
			pingTimer.Start();
			return ping;
		}

		private void PingTimer_Elapsed(object sender, ElapsedEventArgs e)
		{
			if (this.SelectedProfile != null)
				_ping.SendAsync(this.SelectedProfile.Profile.Url, this);
			else
				_pingTimer.Start();
		}

		private void Ping_PingCompleted(object sender, PingCompletedEventArgs e)
		{
			if (!e.Cancelled)
			{
				if (e.Error != null)
				{
					var exception = e.Error;
					while (exception.InnerException != null)
						exception = exception.InnerException;

					_serverPingValue = long.MaxValue;
					this.ServerPing = $"<error>: {exception.Message}";
				}
				else
				{
					switch (e.Reply.Status)
					{
						case IPStatus.Success:
							_serverPingValue = e.Reply.RoundtripTime;
							this.ServerPing = $"{e.Reply.RoundtripTime} ms";
							break;
						case IPStatus.DestinationNetworkUnreachable:
						case IPStatus.DestinationHostUnreachable:
						case IPStatus.DestinationProtocolUnreachable:
						case IPStatus.DestinationPortUnreachable:
							_serverPingValue = long.MaxValue;
							this.ServerPing = "<unreachable>";
							break;
						case IPStatus.TimedOut:
							_serverPingValue = long.MaxValue;
							this.ServerPing = "<timed out>";
							break;

						default:
							_serverPingValue = long.MaxValue;
							this.ServerPing = $"<error>: {e.Reply.Status}";
							break;
					}
				}
			}
			_pingTimer.Start();
		}


		public void SaveProfiles()
		{
			this.SaveProfiles(ProfileStorage.Personal, _personalProfileStorage);
			this.SaveProfiles(ProfileStorage.Public, _publicProfileStorage);

			foreach (var profile in this.Profiles)
				profile.IsModified = false;

			App.ReportStatus("All profiles saved.");
		}

		private void SaveProfiles(ProfileStorage storage, string filename)
		{
			var profiles = this.Profiles.Where(p => p.ProfileStorage == storage)
							   .Select(p => p.Profile)
							   .ToArray();
			if (profiles.Length > 0)
			{
				using (var file = File.Create(filename))
				{
					new XmlSerializer(typeof(LaunchProfile[]))
						.Serialize(file, profiles);
				}
			}
			else if (File.Exists(filename))
			{
				File.Delete(filename);
			}
		}

		private void LoadProfiles()
		{
			_publicProfileStorage = Path.Combine(App.CurrentRootPath, Constants.PublicProfileFilename);
			_personalProfileStorage = Path.Combine(App.CurrentRootPath, Constants.PersonalProfileFilename);
			if (File.Exists(_publicProfileStorage))
				this.LoadProfile(_publicProfileStorage);

			if (File.Exists(_personalProfileStorage))
				this.LoadProfile(_personalProfileStorage);

			if (this.Profiles.Count == 0)
				this.Profiles.Add(this.CreateNewProfile());

			this.SelectedProfile = this.Profiles.FirstOrDefault();
		}

		private void LoadProfile(string profileFile)
		{
			using (var file = File.OpenRead(profileFile))
			{
				foreach (var profile in (LaunchProfile[])new XmlSerializer(typeof(LaunchProfile[])).Deserialize(file))
				{
					this.Profiles.Add(new LaunchProfileViewModel(profile, this.EditMode));
				}
			}
		}

		public void RemoveSelectedProfile()
		{
			this.Profiles.Remove(_selectedProfile);
			this.SelectedProfile = this.Profiles.FirstOrDefault();
			this.SaveProfiles();
			App.ReportStatus("Profile removed, profiles saved.");
		}

		public void AddNewProfile()
		{
			var profile = this.CreateNewProfile();
			this.Profiles.Add(profile);
			this.SelectedProfile = profile;
			App.ReportStatus("Profile created.");
		}

		private LaunchProfileViewModel CreateNewProfile()
		{
			var profile = new LaunchProfileViewModel(new LaunchProfile(), this.EditMode) { IsModified = this.EditMode };
			return profile;
		}

		public bool ConfirmSaveBeforeExit()
		{
			if (!this.Profiles.Any(p => p.IsModified))
				return true;

			switch (
				MessageBox.Show(
					$"The following profiles are modified:\n\n {string.Join("\n", this.Profiles.Where(p => p.IsModified).Select(p => p.ProfileName))} \n\nDo you want to save them before exit?",
					"Save",
					MessageBoxButton.YesNoCancel,
					MessageBoxImage.Question))
			{

				case MessageBoxResult.Cancel:
					return false;
				case MessageBoxResult.Yes:
					this.SaveProfiles();
					return true;
			}

			return true;
		}

		public void DuplicateProfile()
		{
			if (this.SelectedProfile == null)
				return;

			var clone = new LaunchProfileViewModel(this.SelectedProfile.Profile.Clone(), this.EditMode) { IsModified = this.EditMode };
			this.Profiles.Add(clone);
			this.SelectedProfile = clone;
			App.ReportStatus("Profile duplicated.");
		}

		public void LaunchProfile(LaunchProfileViewModel profile = null)
		{
			(profile ?? this.SelectedProfile).Launch(this.AttachDebugger ? this.SelectedDebugger : null);
		}

	}
}
