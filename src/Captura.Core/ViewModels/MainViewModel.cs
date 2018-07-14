﻿using Captura.Models;
using Screna;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;
using System.Windows.Input;
using Captura.Audio;
using Microsoft.Win32;
using Timer = System.Timers.Timer;

namespace Captura.ViewModels
{
    // ReSharper disable once ClassNeverInstantiated.Global
    public partial class MainViewModel : ViewModelBase, IDisposable
    {
        #region Fields
        Timer _timer;
        readonly Timing _timing = new Timing();
        IRecorder _recorder;
        string _currentFileName;
        bool _isVideo;
        public static readonly RectangleConverter RectangleConverter = new RectangleConverter();
        readonly SynchronizationContext _syncContext = SynchronizationContext.Current;
        
        readonly ISystemTray _systemTray;
        readonly IRegionProvider _regionProvider;
        readonly WebcamOverlay _webcamOverlay;
        readonly IMainWindow _mainWindow;

        readonly IDialogService _dialogService;
        #endregion

        readonly IPreviewWindow _previewWindow;

        readonly RememberByName _rememberByName;

        public ScreenShotViewModel ScreenShotViewModel { get; }

        public ICommand ShowPreviewCommand { get; }
        
        public MainViewModel(AudioSource AudioSource,
            VideoViewModel VideoViewModel,
            ISystemTray SystemTray,
            IRegionProvider RegionProvider,
            IWebCamProvider WebCamProvider,
            WebcamOverlay WebcamOverlay,
            IMainWindow MainWindow,
            Settings Settings,
            RecentViewModel RecentViewModel,
            LanguageManager LanguageManager,
            HotKeyManager HotKeyManager,
            CustomOverlaysViewModel CustomOverlays,
            CustomImageOverlaysViewModel CustomImageOverlays,
            IPreviewWindow PreviewWindow,
            CensorOverlaysViewModel CensorOverlays,
            FFmpegLog FFmpegLog,
            IDialogService DialogService,
            RememberByName RememberByName,
            ScreenShotViewModel ScreenShotViewModel) : base(Settings, LanguageManager)
        {
            this.AudioSource = AudioSource;
            this.VideoViewModel = VideoViewModel;
            _systemTray = SystemTray;
            _regionProvider = RegionProvider;
            this.WebCamProvider = WebCamProvider;
            _webcamOverlay = WebcamOverlay;
            _mainWindow = MainWindow;
            this.RecentViewModel = RecentViewModel;
            this.HotKeyManager = HotKeyManager;
            this.CustomOverlays = CustomOverlays;
            this.CustomImageOverlays = CustomImageOverlays;
            _previewWindow = PreviewWindow;
            _dialogService = DialogService;
            _rememberByName = RememberByName;
            this.ScreenShotViewModel = ScreenShotViewModel;
            this.CensorOverlays = CensorOverlays;
            this.FFmpegLog = FFmpegLog;

            ShowPreviewCommand = new DelegateCommand(() => _previewWindow.Show());

            #region Commands
            RecordCommand = new DelegateCommand(OnRecordExecute);
            
            RefreshCommand = new DelegateCommand(OnRefresh);
            
            PauseCommand = new DelegateCommand(OnPauseExecute, false);

            OpenOutputFolderCommand = new DelegateCommand(OpenOutputFolder);

            SelectOutputFolderCommand = new DelegateCommand(SelectOutputFolder);

            ResetFFmpegFolderCommand = new DelegateCommand(() => Settings.FFmpeg.FolderPath = "");
            #endregion

            SystemEvents.PowerModeChanged += SystemEvents_PowerModeChanged;

            Settings.Audio.PropertyChanged += (Sender, Args) =>
            {
                switch (Args.PropertyName)
                {
                    case nameof(Settings.Audio.Enabled):
                    case null:
                    case "":
                        CheckFunctionalityAvailability();
                        break;
                }
            };

            VideoViewModel.PropertyChanged += (Sender, Args) =>
            {
                switch (Args.PropertyName)
                {
                    case nameof(VideoViewModel.SelectedVideoSourceKind):
                    case nameof(VideoViewModel.SelectedVideoSource):
                    case null:
                    case "":
                        CheckFunctionalityAvailability();
                        break;
                }
            };

            // If Output Dircetory is not set. Set it to Documents\Captura\
            if (string.IsNullOrWhiteSpace(Settings.OutPath))
                Settings.OutPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Captura");

            // Create the Output Directory if it does not exist
            Settings.EnsureOutPath();

            // Handle Hoykeys
            HotKeyManager.HotkeyPressed += Service =>
            {
                switch (Service)
                {
                    case ServiceName.Recording:
                        RecordCommand.ExecuteIfCan();
                        break;

                    case ServiceName.Pause:
                        PauseCommand.ExecuteIfCan();
                        break;

                    case ServiceName.ScreenShot:
                        ScreenShotViewModel.ScreenShotCommand.ExecuteIfCan();
                        break;

                    case ServiceName.ActiveScreenShot:
                        ScreenShotViewModel.ScreenShotActiveCommand.ExecuteIfCan();
                        break;

                    case ServiceName.DesktopScreenShot:
                        ScreenShotViewModel.ScreenShotDesktopCommand.ExecuteIfCan();
                        break;

                    case ServiceName.ToggleMouseClicks:
                        Settings.Clicks.Display = !Settings.Clicks.Display;
                        break;

                    case ServiceName.ToggleKeystrokes:
                        Settings.Keystrokes.Display = !Settings.Keystrokes.Display;
                        break;
                }
            };
        }

        async void OnRecordExecute()
        {
            if (RecorderState == RecorderState.NotRecording)
                StartRecording();
            else await StopRecording();
        }

        void OnRefresh()
        {
            WindowProvider.RefreshDesktopSize();

            #region Video Source
            var lastVideoSourceName = VideoViewModel.SelectedVideoSource?.Name;

            VideoViewModel.RefreshVideoSources();

            var matchingVideoSource = VideoViewModel.AvailableVideoSources.FirstOrDefault(M => M.Name == lastVideoSourceName);

            if (matchingVideoSource != null)
            {
                VideoViewModel.SelectedVideoSource = matchingVideoSource;
            }
            #endregion

            #region Video Codec
            var lastVideoCodecName = VideoViewModel.SelectedVideoWriter?.ToString();

            VideoViewModel.RefreshCodecs();

            var matchingVideoCodec = VideoViewModel.AvailableVideoWriters.FirstOrDefault(M => M.ToString() == lastVideoCodecName);

            if (matchingVideoCodec != null)
            {
                VideoViewModel.SelectedVideoWriter = matchingVideoCodec;
            }
            #endregion

            #region Audio
            var lastMicNames = AudioSource.AvailableRecordingSources
                .Where(M => M.Active)
                .Select(M => M.Name)
                .ToArray();

            var lastSpeakerNames = AudioSource.AvailableLoopbackSources
                .Where(M => M.Active)
                .Select(M => M.Name)
                .ToArray();

            AudioSource.Refresh();

            foreach (var source in AudioSource.AvailableRecordingSources)
            {
                source.Active = lastMicNames.Contains(source.Name);
            }

            foreach (var source in AudioSource.AvailableLoopbackSources)
            {
                source.Active = lastSpeakerNames.Contains(source.Name);
            }
            #endregion

            #region Webcam
            var lastWebcamName = WebCamProvider.SelectedCam?.Name;

            WebCamProvider.Refresh();

            var matchingWebcam = WebCamProvider.AvailableCams.FirstOrDefault(M => M.Name == lastWebcamName);

            if (matchingWebcam != null)
            {
                WebCamProvider.SelectedCam = matchingWebcam;
            }
            #endregion

            Status.LocalizationKey = nameof(LanguageManager.Refreshed);
        }

        void SystemEvents_PowerModeChanged(object Sender, PowerModeChangedEventArgs E)
        {
            if (E.Mode == PowerModes.Suspend && RecorderState == RecorderState.Recording)
            {
                OnPauseExecute();
            }
        }

        void OnPauseExecute()
        {
            // Resume
            if (RecorderState == RecorderState.Paused)
            {
                _systemTray.HideNotification();

                _recorder.Start();
                _timing?.Start();
                _timer?.Start();

                RecorderState = RecorderState.Recording;
                Status.LocalizationKey = nameof(LanguageManager.Recording);
            }
            else // Pause
            {
                _recorder.Stop();
                _timer?.Stop();
                _timing?.Pause();

                RecorderState = RecorderState.Paused;
                Status.LocalizationKey = nameof(LanguageManager.Paused);

                _systemTray.ShowTextNotification(Loc.Paused, null);
            }
        }

        bool _persist, _hotkeys, _remembered;

        public void Init(bool Persist, bool Timer, bool Remembered, bool Hotkeys)
        {
            _persist = Persist;
            _hotkeys = Hotkeys;

            if (Timer)
            {
                _timer = new Timer(500);
                _timer.Elapsed += TimerOnElapsed;
            }

            // Register Hotkeys if not console
            if (_hotkeys)
                HotKeyManager.RegisterAll();

            VideoViewModel.Init();

            if (Remembered)
            {
                _remembered = true;

                _rememberByName.RestoreRemembered();
            }
        }

        public void ViewLoaded()
        {
            if (_remembered)
            {
                // Restore Webcam
                if (!string.IsNullOrEmpty(Settings.Video.Webcam))
                {
                    var webcam = WebCamProvider.AvailableCams.FirstOrDefault(C => C.Name == Settings.Video.Webcam);

                    if (webcam != null)
                    {
                        WebCamProvider.SelectedCam = webcam;
                    }
                }
            }

            HotKeyManager.ShowNotRegisteredOnStartup();

            if (AudioSource is NoAudioSource)
            {
                ServiceProvider.MessageProvider.ShowError(
                    "Could not find bass.dll or bassmix.dll.\nAudio Recording will not be available.", "No Audio");
            }
        }
        
        public void Dispose()
        {
            SystemEvents.PowerModeChanged -= SystemEvents_PowerModeChanged;

            if (_hotkeys)
                HotKeyManager.Dispose();

            AudioSource.Dispose();

            RecentViewModel.Dispose();
            
            // Remember things if not console.
            if (_persist)
            {
                _rememberByName.Remember();

                Settings.Save();
            }
        }
        
        async void TimerOnElapsed(object Sender, ElapsedEventArgs Args)
        {
            TimeSpan = TimeSpan.FromSeconds((int)_timing.Elapsed.TotalSeconds);

            var duration = Settings.Duration;

            // If Capture Duration is set and reached
            if (duration > 0 && TimeSpan.TotalSeconds >= Settings.StartDelay / 1000 + duration)
            {
                if (_syncContext != null)
                    _syncContext.Post(async State => await StopRecording(), null);
                else await StopRecording();
            }
        }
        
        void CheckFunctionalityAvailability()
        {
            var audioAvailable = Settings.Audio.Enabled;

            var videoAvailable = !(VideoViewModel.SelectedVideoSourceKind is NoVideoSourceProvider);
            
            RecordCommand.RaiseCanExecuteChanged(audioAvailable || videoAvailable);

            ScreenShotViewModel.ScreenShotCommand.RaiseCanExecuteChanged(videoAvailable);
        }
        
        public bool StartRecording(string FileName = null)
        {
            if (VideoViewModel.SelectedVideoWriterKind is FFmpegWriterProvider ||
                VideoViewModel.SelectedVideoWriterKind is StreamingWriterProvider ||
                (VideoViewModel.SelectedVideoSourceKind is NoVideoSourceProvider && VideoViewModel.SelectedVideoSource is FFmpegAudioItem))
            {
                if (!FFmpegService.FFmpegExists)
                {
                    ServiceProvider.MessageProvider.ShowFFmpegUnavailable();

                    return false;
                }
            }

            if (VideoViewModel.SelectedVideoWriterKind is GifWriterProvider)
            {
                if (Settings.Audio.Enabled)
                {
                    if (!ServiceProvider.MessageProvider.ShowYesNo("Audio won't be included in the recording.\nDo you want to record?", "Gif does not support Audio"))
                    {
                        return false;
                    }
                }
            }

            IImageProvider imgProvider;

            try
            {
                imgProvider = GetImageProvider();
            }
            catch (NotSupportedException e) when (VideoViewModel.SelectedVideoSourceKind is DeskDuplSourceProvider)
            {
                var yes = ServiceProvider.MessageProvider.ShowYesNo($"{e.Message}\n\nDo you want to turn off Desktop Duplication.", Loc.ErrorOccurred);

                if (yes)
                    VideoViewModel.SelectedVideoSourceKind = VideoViewModel.AvailableVideoSourceKinds[0];

                return false;
            }
            catch (Exception e)
            {
                ServiceProvider.MessageProvider.ShowException(e, e.Message);

                return false;
            }

            // Window Picker or Screen Picker Cancelled
            if ((VideoViewModel.SelectedVideoSource is WindowPickerItem
                || VideoViewModel.SelectedVideoSource is ScreenPickerItem) && imgProvider == null)
            {
                return false;
            }

            if (VideoViewModel.SelectedVideoWriterKind is GifWriterProvider
                && Settings.Gif.VariableFrameRate
                && imgProvider is DeskDuplImageProvider deskDuplImageProvider)
            {
                deskDuplImageProvider.Timeout = 5000;
            }

            Settings.EnsureOutPath();
            
            _isVideo = !(VideoViewModel.SelectedVideoSourceKind is NoVideoSourceProvider);
            
            var extension = VideoViewModel.SelectedVideoWriter.Extension;

            if (VideoViewModel.SelectedVideoSource is NoVideoItem x)
                extension = x.Extension;

            _currentFileName = FileName ?? Path.Combine(Settings.OutPath, $"{DateTime.Now:yyyy-MM-dd-HH-mm-ss}{extension}");

            IAudioProvider audioProvider = null;

            try
            {
                if (Settings.Audio.Enabled)
                {
                    audioProvider = AudioSource.GetAudioProvider(Settings.Video.FrameRate);
                }
            }
            catch (Exception e)
            {
                ServiceProvider.MessageProvider.ShowException(e, e.Message);

                imgProvider?.Dispose();

                return false;
            }

            IVideoFileWriter videoEncoder;

            try
            {
                videoEncoder = GetVideoFileWriter(imgProvider, audioProvider);
            }
            catch (Exception e)
            {
                ServiceProvider.MessageProvider.ShowException(e, e.Message);

                imgProvider?.Dispose();
                audioProvider?.Dispose();

                return false;
            }

            switch (videoEncoder)
            {
                case GifWriter gif when Settings.Gif.VariableFrameRate:
                    _recorder = new VFRGifRecorder(gif, imgProvider);
                    break;

                case WithPreviewWriter previewWriter when previewWriter.OriginalWriter is GifWriter gif && Settings.Gif.VariableFrameRate:
                    _recorder = new VFRGifRecorder(gif, imgProvider);
                    break;

                default:
                    if (_isVideo)
                    {
                        _recorder = new Recorder(videoEncoder, imgProvider, Settings.Video.FrameRate, audioProvider);
                    }
                    else if (VideoViewModel.SelectedVideoSource is NoVideoItem audioWriter)
                    {
                        _recorder = new Recorder(audioWriter.GetAudioFileWriter(_currentFileName, audioProvider?.WaveFormat, Settings.Audio.Quality), audioProvider);
                    }
                    break;
            }

            if (VideoViewModel.SelectedVideoSourceKind is RegionSourceProvider)
                _regionProvider.Lock();

            _systemTray.HideNotification();

            if (Settings.UI.MinimizeOnStart)
                _mainWindow.IsMinimized = true;

            RecorderState = RecorderState.Recording;

            _timer?.Stop();
            TimeSpan = TimeSpan.Zero;

            Status.LocalizationKey = Settings.StartDelay > 0 ? nameof(LanguageManager.Waiting) : nameof(LanguageManager.Recording);

            _recorder.ErrorOccurred += E =>
            {
                if (_syncContext != null)
                    _syncContext.Post(S => OnErrorOccurred(E), null);
                else OnErrorOccurred(E);
            };
            
            if (Settings.StartDelay > 0)
            {
                Task.Factory.StartNew(async () =>
                {
                    await Task.Delay(Settings.StartDelay);

                    Status.LocalizationKey = nameof(LanguageManager.Recording);

                    _recorder.Start();
                });
            }
            else _recorder.Start();

            _timing?.Start();
            _timer?.Start();

            return true;
        }

        void OnErrorOccurred(Exception E)
        {
            var cancelled = E is OperationCanceledException;

            if (!cancelled)
                Status.LocalizationKey = nameof(LanguageManager.ErrorOccurred);
                        
            AfterRecording();

            if (!cancelled)
                ServiceProvider.MessageProvider.ShowException(E, E.Message);
        }

        void AfterRecording()
        {
            RecorderState = RecorderState.NotRecording;

            _recorder = null;

            _timer?.Stop();
            _timing.Stop();
            
            if (Settings.UI.MinimizeOnStart)
                _mainWindow.IsMinimized = false;

            if (VideoViewModel.SelectedVideoSourceKind is RegionSourceProvider)
                _regionProvider.Release();
        }

        IVideoFileWriter GetVideoFileWriter(IImageProvider ImgProvider, IAudioProvider AudioProvider)
        {
            if (VideoViewModel.SelectedVideoSourceKind is NoVideoSourceProvider)
                return null;

            _previewWindow.Init(ImgProvider.Width, ImgProvider.Height);

            return new WithPreviewWriter(VideoViewModel.SelectedVideoWriter.GetVideoFileWriter(new VideoWriterArgs
            {
                FileName = _currentFileName,
                FrameRate = Settings.Video.FrameRate,
                VideoQuality = Settings.Video.Quality,
                ImageProvider = ImgProvider,
                AudioQuality = Settings.Audio.Quality,
                AudioProvider = AudioProvider
            }), _previewWindow);
        }
        
        IImageProvider GetImageProvider()
        {
            Func<Point, Point> transform = P => P;

            var imageProvider = VideoViewModel.SelectedVideoSource?.GetImageProvider(Settings.IncludeCursor, out transform);

            if (imageProvider == null)
                return null;

            var overlays = new List<IOverlay>
            {
                new CensorOverlay(Settings.Censored),
                _webcamOverlay,
                new MousePointerOverlay(Settings.MousePointerOverlay)
            };

            if (MouseKeyHookAvailable)
                overlays.Add(new MouseKeyHook(Settings.Clicks, Settings.Keystrokes));
            
            overlays.Add(new ElapsedOverlay(Settings.Elapsed, () => TimeSpan));
            
            // Custom Overlays
            overlays.AddRange(CustomOverlays.Collection.Select(M => new CustomOverlay(M)));

            // Custom Image Overlays
            foreach (var overlay in CustomImageOverlays.Collection.Where(M => M.Display))
            {
                try
                {
                    overlays.Add(new CustomImageOverlay(overlay));
                }
                catch
                {
                    // Ignore Errors like Image not found, Invalid Image
                }
            }

            return new OverlayedImageProvider(imageProvider, transform, overlays.ToArray());
        }

        readonly object _stopRecTaskLock = new object();
        readonly List<Task> _stopRecTasks = new List<Task>();

        public int RunningStopRecordingCount
        {
            get
            {
                lock (_stopRecTaskLock)
                {
                    return _stopRecTasks.Count(M => !M.IsCompleted);
                }
            }
        }

        public async Task StopRecording()
        {
            Status.LocalizationKey = nameof(LanguageManager.Stopped);

            RecentItemViewModel savingRecentItem = null;

            // Assume saving to file only when extension is present
            if (!string.IsNullOrWhiteSpace(VideoViewModel.SelectedVideoWriter.Extension))
            {
                savingRecentItem = RecentViewModel.Add(_currentFileName, _isVideo ? RecentItemType.Video : RecentItemType.Audio, true);
            }
            
            // Reference Recorder as it will be set to null
            var rec = _recorder;
            
            var task = Task.Run(() => rec.Dispose());

            lock (_stopRecTaskLock)
            {
                _stopRecTasks.Add(task);
            }
            
            AfterRecording();

            try
            {
                // Ensure saved
                await task;

                lock (_stopRecTaskLock)
                {
                    _stopRecTasks.Remove(task);
                }
            }
            catch (Exception e)
            {
                ServiceProvider.MessageProvider.ShowException(e, "Error occurred when stopping recording.\nThis might sometimes occur if you stop recording just as soon as you start it.");

                return;
            }

            if (savingRecentItem != null)
            {
                // After Save
                savingRecentItem.Saved();

                if (Settings.CopyOutPathToClipboard)
                    savingRecentItem.FilePath.WriteToClipboard();

                _systemTray.ShowTextNotification((savingRecentItem.ItemType == RecentItemType.Video ? Loc.VideoSaved : Loc.AudioSaved) + ": " +
                    Path.GetFileName(savingRecentItem.FilePath), () =>
                    {
                        ServiceProvider.LaunchFile(new ProcessStartInfo(savingRecentItem.FilePath));
                    });
            }
        }
    }
}