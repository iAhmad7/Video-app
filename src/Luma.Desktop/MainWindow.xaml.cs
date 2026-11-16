using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LibVLCSharp.Shared;
using MediaPlayer = LibVLCSharp.Shared.MediaPlayer;
using Luma.Core;
using Microsoft.Win32;

namespace Luma.Desktop;

public sealed class LibraryItem(MediaInfo info) : INotifyPropertyChanged
{
    public MediaInfo Info { get; } = info;
    ImageSource? thumbnail;
    public ImageSource? Thumbnail { get => thumbnail; set { thumbnail = value; PropertyChanged?.Invoke(this, new(nameof(Thumbnail))); } }
    public event PropertyChangedEventHandler? PropertyChanged;
    public string Name => Path.GetFileName(Info.Path);
    public string SmallInfo => $"{Format.Bytes(Info.Size)} · {Format.Time(Info.Duration)}";
}
public partial class MainWindow : Window
{
    readonly SettingsStore store;
    AppSettings settings;
    readonly ObservableCollection<LibraryItem> library = [];
    readonly ObservableCollection<Job> jobs = [];
    readonly CancellationTokenSource lifetime = new();
    readonly SemaphoreSlim previewGate = new(1, 1);
    readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(350) };
    MediaTools? tools;
    JobQueue? queue;
    LibVLC? vlc;
    MediaPlayer? player;
    CancellationTokenSource? thumbnailCts, validationCts;
    Operation operation = Operation.Convert;
    string page = "Convert";
    bool ready, seeking, closing, allowClose, importing, configuring, validating, startingPreview;
    int selectionVersion;
    MediaInfo? Selected => (MediaList.SelectedItem as LibraryItem)?.Info;

    public MainWindow()
    {
        InitializeComponent();
        store = new();
        try { settings = store.Load(); } catch (ToolException e) { settings = new(); Loaded += (_, _) => ShowText("Settings recovery", e.ToString()); }
        MediaList.ItemsSource = library; QueueList.ItemsSource = jobs;
        ContainerBox.SelectedIndex = CodecBox.SelectedIndex = CompressionBox.SelectedIndex = ResolutionBox.SelectedIndex = TargetUnitBox.SelectedIndex = UtilityBox.SelectedIndex = AudioFormatBox.SelectedIndex = RepairBox.SelectedIndex = TransformBox.SelectedIndex = 0;
        PresetBox.SelectedIndex = SpeedBox.SelectedIndex = 1;
        LoadSettingsControls(); ApplyTheme();
        if (settings.RestoreWindow) { Width = Math.Min(settings.WindowWidth, SystemParameters.WorkArea.Width); Height = Math.Min(settings.WindowHeight, SystemParameters.WorkArea.Height); }
        if (settings.RememberPreferences && settings.LastOptions is { } o) RestoreOptions(o);
        timer.Tick += Timer_Tick;
        SeekSlider.PreviewKeyUp += (_, _) => SeekToSlider();
        DiagnosticLog.LogFailure += OnLogFailure;
        ready = true; SetPage("Convert");
        Loaded += async (_, _) => await Guard(async () => { await ConfigureToolsAsync(); DiagnosticLog.Cleanup(); });
    }
    void OnLogFailure(string text) => Dispatcher.BeginInvoke(() => StatusText.Text = "Diagnostic log could not be written: " + text);
    async Task Guard(Func<Task> action)
    {
        try { await action(); }
        catch (OperationCanceledException) { StatusText.Text = "Operation cancelled or timed out. Original files are preserved."; }
        catch (Exception e) { Error(e); }
    }
    void Error(Exception e)
    {
        DiagnosticLog.Write(e.ToString());
        StatusText.Text = e.Message;
        ShowText("Luma · operation could not finish", e + "\n\nRecovery: check the chosen file, FFmpeg paths, output permissions, codec/container combination and free disk space.\nLogs: " + DiagnosticLog.DirectoryPath);
    }
    void ShowText(string title, string text)
    {
        var dialog = new Window { Title = title, Owner = this, Width = 790, Height = 540, MinWidth = 480, MinHeight = 300, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var panel = new DockPanel { Margin = new Thickness(18) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var copy = new Button { Content = "Copy details", Margin = new Thickness(0, 0, 10, 0) };
        copy.Click += (_, _) => { try { Clipboard.SetText(text); } catch (Exception e) { DiagnosticLog.Write("Clipboard: " + e); } };
        var close = new Button { Content = "Close", IsCancel = true }; close.Click += (_, _) => dialog.Close();
        buttons.Children.Add(copy); buttons.Children.Add(close); DockPanel.SetDock(buttons, Dock.Bottom); panel.Children.Add(buttons);
        panel.Children.Add(new TextBox { Text = text, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, FontFamily = new FontFamily("Consolas"), FontSize = 12 });
        dialog.Content = panel; dialog.ShowDialog();
    }
    void Navigate(object sender, RoutedEventArgs e) { if (((Button)sender).Tag is string key) SetPage(key); }
    void SetPage(string key)
    {
        page = key;
        foreach (var button in Navigation.Children.OfType<Button>()) button.Background = (Brush)FindResource(button.Tag?.ToString() == key ? "Panel" : "Sidebar");
        WorkspacePage.Visibility = key is "Info" or "Queue" or "Settings" ? Visibility.Collapsed : Visibility.Visible;
        InfoPage.Visibility = key == "Info" ? Visibility.Visible : Visibility.Collapsed;
        QueuePage.Visibility = key == "Queue" ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = key == "Settings" ? Visibility.Visible : Visibility.Collapsed;
        var titles = new Dictionary<string, (string, string)>
        {
            ["Convert"] = ("Video converter", "A new format. The same story."), ["Compress"] = ("Video compressor", "Less space. More room for what matters."),
            ["Remux"] = ("Video remuxer", "Change the container. Keep the encoded quality."), ["Cut"] = ("Video cutter", "Keep the part you need. Always keep the original."),
            ["Repair"] = ("Video repair", "Inspect, rebuild and recover what the container allows."), ["Info"] = ("Media information", "The complete FFprobe report. Missing values stay missing."),
            ["Utilities"] = ("Audio & image tools", "Small tools for the everyday tasks."), ["Queue"] = ("Processing queue", "Real progress. Clear results. Safe cancellation."), ["Settings"] = ("Settings", "Make Luma feel at home on your PC.")
        };
        var title = titles[key]; PageTitle.Text = title.Item1; PageSubtitle.Text = title.Item2;
        if (Enum.TryParse<Operation>(key, out var op)) operation = op;
        if (key == "Utilities") UpdateUtility();
        if (key == "Repair" && !ready) ContainerBox.SelectedIndex = 1;
        if (key == "Repair" && Text(ContainerBox) != "mkv") ContainerBox.SelectedIndex = 1;
        if (key == "Info") RefreshInfo();
        if (key == "Settings") RefreshStorage();
        UpdateSections(); UpdatePlan();
    }
    static string Text(ComboBox combo) => (combo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "";
    static string SelectedTag(ComboBox combo) => (combo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "";
    static void Choose(ComboBox combo, string value, bool tag = false)
    {
        foreach (var item in combo.Items.OfType<ComboBoxItem>()) if ((tag ? item.Tag : item.Content)?.ToString() == value) { combo.SelectedItem = item; return; }
    }
    void UpdateUtility()
    {
        operation = UtilityBox.SelectedIndex switch { 1 => Operation.ExtractFrame, 2 => Operation.Transform, _ => Operation.ExtractAudio };
    }
    void UpdateSections()
    {
        if (!ready) return;
        if (page == "Utilities") UpdateUtility();
        static void Show(UIElement e, bool yes) => e.Visibility = yes ? Visibility.Visible : Visibility.Collapsed;
        var encoding = operation is Operation.Convert or Operation.Compress or Operation.Transform || operation == Operation.Cut && PreciseBox.IsChecked == true;
        Show(UtilitySection, page == "Utilities"); Show(AudioFormatBox, operation == Operation.ExtractAudio); Show(AudioFormatLabel, operation == Operation.ExtractAudio);
        Show(EncodingSection, operation is not (Operation.ExtractAudio or Operation.ExtractFrame));
        Show(CodecSection, encoding); Show(QualitySection, encoding); Show(PreferCopyBox, operation == Operation.Convert);
        Show(CompressionSection, operation == Operation.Compress); Show(ReductionSection, CompressionBox.SelectedIndex == 1); Show(TargetSection, CompressionBox.SelectedIndex == 2);
        Show(CutSection, operation is Operation.Cut or Operation.ExtractFrame); Show(EndSection, operation != Operation.ExtractFrame); Show(PreciseBox, operation == Operation.Cut); Show(CutHelp, operation == Operation.Cut);
        PreciseBox.IsEnabled = operation == Operation.Cut;
        Show(RepairSection, operation == Operation.Repair); Show(TransformSection, operation == Operation.Transform);
        Show(AdvancedSection, operation != Operation.ExtractFrame);
        FpsBox.IsEnabled = BitrateBox.IsEnabled = CrfBox.IsEnabled = SpeedBox.IsEnabled = encoding;
        OperationHeading.Text = operation switch { Operation.Remux => "Container & track selection", Operation.Repair => "Recovery options", Operation.Cut => "Cut range & output", _ => "Output settings" };
    }
    void Options_Changed(object sender, RoutedEventArgs e) { if (ready) { UpdateSections(); UpdatePlan(); } }
    void TextOption_Changed(object sender, TextChangedEventArgs e) { if (ready) UpdatePlan(); }
    void Reduction_Changed(object sender, RoutedPropertyChangedEventArgs<double> e) { if (ready) UpdatePlan(); }
    static double Numeric(string value, string label)
    {
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) || !double.IsFinite(n)) throw new InvalidOperationException(label + " must be a number (use a dot for decimals)."); return n;
    }
    static int Integer(string value, string label, int min, int max)
    {
        if (!int.TryParse(value, out var n) || n < min || n > max) throw new InvalidOperationException($"{label} must be an integer from {min} to {max}."); return n;
    }
    static double Timestamp(string value)
    {
        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) && double.IsFinite(n)) return n;
        var parts = value.Split(':');
        if (parts.Length == 3 && long.TryParse(parts[0], out var h) && h >= 0 && h < 1000000 && int.TryParse(parts[1], out var m) && m >= 0 && m < 60)
        {
            var seconds = Numeric(parts[2], "Seconds");
            if (seconds >= 0 && seconds < 60) return h * 3600.0 + m * 60 + seconds;
        }
        throw new InvalidOperationException("Enter a timestamp as hh:mm:ss.fff or seconds.");
    }
    static int[]? TrackIds(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        return text.Split(',').Select(v => Integer(v.Trim(), "Stream ID", 0, 9999)).Distinct().ToArray();
    }
    JobOptions ReadOptions()
    {
        var target = operation == Operation.Compress && CompressionBox.SelectedIndex == 2 ? Numeric(TargetSizeBox.Text, "Target size") * (TargetUnitBox.SelectedIndex == 1 ? 1073741824.0 : 1048576.0) : 0;
        if (target < 0 || target > long.MaxValue / 2) throw new InvalidOperationException("Target size is outside the supported range.");
        var encoding = operation is Operation.Convert or Operation.Compress or Operation.Transform || operation == Operation.Cut && PreciseBox.IsChecked == true;
        return new()
        {
            Operation = operation, Container = Text(ContainerBox), Codec = SelectedTag(CodecBox), Preset = Text(PresetBox), Speed = Text(SpeedBox),
            Crf = encoding ? Integer(CrfBox.Text, "Custom CRF", 0, 51) : 23,
            Height = encoding ? int.Parse(SelectedTag(ResolutionBox), CultureInfo.InvariantCulture) : 0,
            Fps = encoding ? Numeric(FpsBox.Text, "Frame rate") : 0,
            VideoKbps = encoding ? Integer(BitrateBox.Text, "Bitrate", 0, 200000) : 0,
            Compression = (CompressionMode)Math.Max(0, CompressionBox.SelectedIndex), Reduction = ReductionSlider.Value, TargetBytes = (long)target,
            PreferCopy = PreferCopyBox.IsChecked == true, Precise = PreciseBox.IsChecked == true,
            Start = operation is Operation.Cut or Operation.ExtractFrame ? Timestamp(StartBox.Text) : 0,
            End = operation == Operation.Cut ? Timestamp(EndBox.Text) : 0,
            RemoveAudio = RemoveAudioBox.IsChecked == true, RemoveSubtitles = RemoveSubsBox.IsChecked == true,
            AudioTracks = TrackIds(AudioTracksBox.Text), SubtitleTracks = TrackIds(SubtitleTracksBox.Text),
            DeepValidateOutput = operation == Operation.Repair && DeepAfterBox.IsChecked == true,
            RepairMode = Text(RepairBox), AudioFormat = Text(AudioFormatBox), Transform = Text(TransformBox), Crop = CropBox.Text.Trim(),
            OutputDirectory = settings.OutputDirectory, Suffix = settings.FilenameSuffix, Acceleration = settings.Acceleration, Threads = settings.Threads, LowPriority = settings.LowPriority
        };
    }
    void UpdatePlan()
    {
        if (!ready) return;
        ReductionLabel.Text = $"Reduce final file size by {ReductionSlider.Value:0}%";
        if (Selected is not { } media) { PlanText.Text = "Select a file to check output compatibility."; PlanWarning.Text = ""; TargetEstimate.Text = ""; return; }
        try
        {
            var options = ReadOptions();
            var target = Planner.Target(media, options);
            TargetEstimate.Text = $"Original: {Format.Bytes(media.Size)}\n" + (target.HasValue ? $"Calculated target: {Format.Bytes(target.Value)} (approximate)" : "Quality mode: output size is content-dependent; no reliable size estimate.");
            var plan = Planner.Create(media, options, tools?.QsvAvailable == true);
            PlanText.Text = plan.Explanation; PlanWarning.Text = plan.Warning;
        }
        catch (Exception e) when (e is InvalidOperationException or FormatException or OverflowException) { PlanText.Text = "Check your settings before adding a job."; PlanWarning.Text = e.Message; }
    }
    void RestoreOptions(JobOptions o)
    {
        Choose(ContainerBox, o.Container); Choose(CodecBox, o.Codec, true); Choose(PresetBox, o.Preset); Choose(SpeedBox, o.Speed);
        Choose(ResolutionBox, o.Height.ToString(CultureInfo.InvariantCulture), true); CrfBox.Text = o.Crf.ToString(); FpsBox.Text = Format.Number(o.Fps); BitrateBox.Text = o.VideoKbps.ToString();
        CompressionBox.SelectedIndex = (int)o.Compression; ReductionSlider.Value = o.Reduction; PreferCopyBox.IsChecked = o.PreferCopy;
        RemoveAudioBox.IsChecked = o.RemoveAudio; RemoveSubsBox.IsChecked = o.RemoveSubtitles;
    }
    MediaTools RequireTools() => tools ?? throw new InvalidOperationException("FFmpeg tools are not configured. Open Settings, select your existing ffmpeg.exe and ffprobe.exe, and click Verify paths & capabilities.");
    async Task ConfigureToolsAsync()
    {
        if (configuring || importing || validating || queue?.HasWork == true) throw new InvalidOperationException("Wait for imports, validation and queued jobs to finish before changing media tools.");
        configuring = true; ImportButton.IsEnabled = EnqueueButton.IsEnabled = false;
        try
        {
            StatusText.Text = "Verifying existing FFmpeg installation…";
            var ffmpeg = MediaTools.Locate(FfmpegBox.Text.Trim(), "ffmpeg"); var ffprobe = MediaTools.Locate(FfprobeBox.Text.Trim(), "ffprobe", ffmpeg);
            var candidate = new MediaTools(ffmpeg, ffprobe); await candidate.VerifyAsync(lifetime.Token); await candidate.TestGpuAsync(lifetime.Token);
            if (queue != null) await queue.DisposeAsync();
            tools = candidate; queue = new(tools, settings.MaxJobs); queue.Finished += JobFinished;
            FfmpegBox.Text = ffmpeg; FfprobeBox.Text = ffprobe;
            ToolReport.Text = $"FFmpeg: {ffmpeg}\nFFprobe: {ffprobe}\n{tools.Version}\n{tools.ProbeVersion}\n\n{tools.GpuReport}";
            ToolBadge.Text = tools.QsvAvailable ? "FFmpeg ready · Intel QSV" : "FFmpeg ready · CPU";
            StatusText.Text = "Ready · tools verified. " + (tools.QsvAvailable ? "Quick Sync passed." : "CPU encoding available."); UpdatePlan();
        }
        catch { ToolBadge.Text = tools == null ? "Choose FFmpeg in Settings" : "Using previous verified tools"; throw; }
        finally { configuring = false; ImportButton.IsEnabled = EnqueueButton.IsEnabled = true; }
    }
    async void Import_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Multiselect = true, Title = "Add videos", Filter = "Media files|*.mp4;*.mkv;*.mov;*.avi;*.webm;*.flv;*.ts;*.mts;*.m2ts;*.wmv;*.mpg;*.mpeg;*.m4v;*.vob;*.mp3;*.m4a;*.wav;*.flac|All files|*.*" };
        if (dialog.ShowDialog(this) == true) await Guard(() => ImportAsync(dialog.FileNames));
    }
    void Window_DragOver(object sender, DragEventArgs e) { e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; }
    async void Window_Drop(object sender, DragEventArgs e) { if (e.Data.GetData(DataFormats.FileDrop) is string[] paths) await Guard(() => ImportAsync(paths)); }
    async Task ImportAsync(IEnumerable<string> paths)
    {
        if (importing || configuring) throw new InvalidOperationException("A tool check or import is already running. Please wait.");
        var service = RequireTools(); importing = true; ImportButton.IsEnabled = false;
        var errors = new List<string>(); LibraryItem? first = null;
        try
        {
            foreach (var path in paths)
            {
                if (library.Any(x => string.Equals(x.Info.Path, Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase))) continue;
                try
                {
                    StatusText.Text = "Reading " + Path.GetFileName(path) + "…";
                    var info = await service.ProbeAsync(path, lifetime.Token); var item = new LibraryItem(info); library.Add(item); first ??= item;
                }
                catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { throw; }
                catch (Exception e) { errors.Add(Path.GetFileName(path) + ": " + e); DiagnosticLog.Write("Import: " + e); }
            }
            LibraryCount.Text = $"{library.Count} file{(library.Count == 1 ? "" : "s")}";
            if (first != null) { MediaList.SelectedItem = first; if (page is "Settings" or "Queue" or "Info") SetPage("Convert"); }
            StatusText.Text = $"{library.Count} file(s) in workspace. Use Ctrl or Shift to select a batch.";
            if (errors.Count > 0) ShowText("Some files could not be imported", string.Join("\n\n", errors));
        }
        finally { importing = false; ImportButton.IsEnabled = true; }
    }
    async void Media_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!ready) return;
        var version = ++selectionVersion; thumbnailCts?.Cancel();
        await Guard(async () =>
        {
            await StopPlayerAsync();
            if (version != selectionVersion) return;
            PreviewImage.Source = null; VideoView.Visibility = Visibility.Collapsed; PreviewPlaceholder.Visibility = Visibility.Visible; PreviewPlaceholder.Text = "Select a video to see its preview";
            if (Selected is not { } info) { UpdatePlan(); return; }
            SelectedSummary.Text = info.Summary; PreviewPlaceholder.Text = "Preparing preview…";
            SeekSlider.Value = 0; SeekSlider.Maximum = Math.Max(1, info.Duration); PlaybackTime.Text = "00:00 / " + Format.Time(info.Duration);
            EndBox.Text = info.Duration > 0 ? Format.Time(info.Duration) : "00:00:10.000";
            UpdatePlan(); RefreshInfo();
            if (info.Video == null) { PreviewPlaceholder.Text = "Audio file · preview in your external player"; return; }
            thumbnailCts?.Dispose(); thumbnailCts = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            try
            {
                var path = await RequireTools().ThumbnailAsync(info, store.CacheDirectory, settings.CacheMiB, thumbnailCts.Token);
                if (version != selectionVersion) return;
                var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.DecodePixelWidth = 640; bitmap.UriSource = new Uri(path); bitmap.EndInit(); bitmap.Freeze();
                PreviewImage.Source = bitmap;
                if (MediaList.SelectedItem is LibraryItem current) current.Thumbnail = bitmap;
                foreach (var older in library.Where(x => x.Thumbnail != null && x != MediaList.SelectedItem).Skip(31)) older.Thumbnail = null;
                PreviewPlaceholder.Visibility = Visibility.Collapsed;
            }
            catch (OperationCanceledException) { }
            catch (Exception error) { DiagnosticLog.Write("Preview thumbnail: " + error); if (version == selectionVersion) { PreviewPlaceholder.Text = "Preview unavailable · try Play or Open externally"; StatusText.Text = error.Message; } }
        });
    }
    void RemoveMedia_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in MediaList.SelectedItems.Cast<LibraryItem>().ToArray()) library.Remove(item);
        LibraryCount.Text = $"{library.Count} files";
    }
    async Task StopPlayerAsync()
    {
        timer.Stop(); PlayButton.Content = "▶";
        await previewGate.WaitAsync();
        try { if (player is { } p) await Task.Run(() => p.Stop()); }
        finally { previewGate.Release(); }
    }
    void InitializePlayer()
    {
        if (player != null) return;
        LibVLCSharp.Shared.Core.Initialize(Path.Combine(AppContext.BaseDirectory, "libvlc", "win-x64"));
        vlc = new LibVLC("--quiet", "--no-video-title-show", "--no-sub-autodetect-file", "--avcodec-threads=2", "--file-caching=350");
        player = new MediaPlayer(vlc); VideoView.MediaPlayer = player;
        player.EncounteredError += (_, _) => Dispatcher.BeginInvoke(() => { timer.Stop(); VideoView.Visibility = Visibility.Collapsed; PreviewPlaceholder.Text = "This file could not be previewed. Use Open externally."; PreviewPlaceholder.Visibility = Visibility.Visible; StatusText.Text = "Playback failed. Processing is still available; open the file in your default external player."; DiagnosticLog.Write("LibVLC playback failed for " + Selected?.Path); });
        player.EndReached += (_, _) => Dispatcher.BeginInvoke(() => { timer.Stop(); PlayButton.Content = "▶"; });
    }
    async void Play_Click(object sender, RoutedEventArgs e) => await Guard(async () =>
    {
        if (Selected is not { } info || startingPreview || closing) return;
        var playVersion = selectionVersion; startingPreview = true;
        try
        {
        try { InitializePlayer(); }
        catch (Exception ex) { DiagnosticLog.Write("LibVLC initialization: " + ex); ShowText("Preview unavailable", "The embedded playback backend could not start. Extract the whole portable ZIP, including the libvlc folder. You can still process videos and use Open externally.\n\n" + ex.Message); return; }
        if (player!.IsPlaying) { player.Pause(); PlayButton.Content = "▶"; return; }
        VideoView.Visibility = Visibility.Visible;
        if (player.State == VLCState.Paused) player.Play();
        else
        {
            await StopPlayerAsync();
            if (playVersion != selectionVersion || closing) return;
            using var media = new Media(vlc!, info.Path, FromType.FromPath);
            if (!player.Play(media)) throw new InvalidOperationException("Preview could not start. Open the file in your default external player; processing is still available.");
        }
        PlayButton.Content = "Ⅱ"; timer.Start();
        }
        finally { startingPreview = false; }
    });
    void Timer_Tick(object? sender, EventArgs e)
    {
        if (player == null || seeking) return;
        var time = Math.Max(0, player.Time / 1000.0); SeekSlider.Value = Math.Min(SeekSlider.Maximum, time);
        PlaybackTime.Text = Format.Time(time) + " / " + Format.Time(Selected?.Duration ?? 0);
    }
    void Seek_Down(object sender, MouseButtonEventArgs e) => seeking = true;
    void Seek_Up(object sender, MouseButtonEventArgs e) { SeekToSlider(); seeking = false; }
    void SeekToSlider() { if (player?.IsSeekable == true) player.Time = (long)(SeekSlider.Value * 1000); else if (player?.IsPlaying == true) StatusText.Text = "This preview is not seekable. Try Video repair or open it externally."; }
    void MarkStart_Click(object sender, RoutedEventArgs e) => StartBox.Text = Format.Time(SeekSlider.Value);
    void MarkEnd_Click(object sender, RoutedEventArgs e) => EndBox.Text = Format.Time(SeekSlider.Value);
    void External_Click(object sender, RoutedEventArgs e) { if (Selected != null) OpenPath(Selected.Path); }
    void OpenPath(string path) { try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); } catch (Exception ex) { Error(ex); } }
    async void Enqueue_Click(object sender, RoutedEventArgs e) => await Guard(async () =>
    {
        RequireTools(); if (queue == null || configuring || validating) throw new InvalidOperationException("Wait for media tools to finish configuring.");
        var selected = MediaList.SelectedItems.Cast<LibraryItem>().ToArray();
        if (selected.Length == 0) throw new InvalidOperationException("Add and select at least one file.");
        var options = ReadOptions();
        // Validate the whole batch before mutating the queue.
        foreach (var item in selected) Planner.Create(item.Info, options, tools!.QsvAvailable);
        await StopPlayerAsync();
        if (settings.RememberPreferences) { settings.LastOptions = options; store.Save(settings); }
        foreach (var item in selected)
        {
            var job = queue.Enqueue(item.Info, options); jobs.Add(job);
        }
        SetPage("Queue"); StatusText.Text = $"Added {selected.Length} job(s). Outputs are verified before being published.";
    });
    void JobFinished(Job job) => Dispatcher.BeginInvoke(() =>
    {
        StatusText.Text = $"{job.Name}: {job.State}";
        if (QueueList.SelectedItem == job) JobDetails.Text = job.Detail;
        if (job.State == JobState.Completed && settings.OpenOutput) OpenPath(Path.GetDirectoryName(job.Output)!);
    });
    void Job_Changed(object sender, SelectionChangedEventArgs e) => JobDetails.Text = (QueueList.SelectedItem as Job)?.Detail ?? "";
    void CancelJob_Click(object sender, RoutedEventArgs e) { if (QueueList.SelectedItem is Job job) queue?.Cancel(job); }
    void CancelAll_Click(object sender, RoutedEventArgs e) => queue?.CancelAll();
    void ClearJobs_Click(object sender, RoutedEventArgs e) { foreach (var job in jobs.Where(j => j.State is JobState.Completed or JobState.Failed or JobState.Cancelled).ToArray()) jobs.Remove(job); }
    void OpenOutput_Click(object sender, RoutedEventArgs e) { if (QueueList.SelectedItem is Job { State: JobState.Completed } job && File.Exists(job.Output)) OpenPath(Path.GetDirectoryName(job.Output)!); else StatusText.Text = "Select a successfully completed job."; }
    async void QuickValidate_Click(object sender, RoutedEventArgs e) => await Guard(async () => { if (Selected != null) await ValidateMediaAsync(Selected, settings.DeepValidation); });
    async void DeepValidate_Click(object sender, RoutedEventArgs e) => await Guard(async () => { if (Selected != null) await ValidateMediaAsync(Selected, true); });
    void CancelValidation_Click(object sender, RoutedEventArgs e) => validationCts?.Cancel();
    async Task ValidateMediaAsync(MediaInfo info, bool deep)
    {
        if (validating || configuring || queue?.HasWork == true) throw new InvalidOperationException("Wait for media jobs and tool checks to finish before validation.");
        validating = true; validationCts = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); CancelValidationButton.IsEnabled = true;
        try
        {
            await StopPlayerAsync(); StatusText.Text = deep ? "Deep validation reads and decodes the entire video…" : "Checking container metadata…";
            ValidationReport.Visibility = Visibility.Visible; ValidationReport.Text = "Validation running…";
            var report = await RequireTools().ValidateAsync(info, deep, settings.Threads, validationCts.Token, new Progress<JobProgress>(p => StatusText.Text = "Validation: " + p.Detail));
            ValidationReport.Text = report; ShowText("Media validation", report); StatusText.Text = "Validation finished. Read the report for warnings.";
        }
        finally { validating = false; validationCts.Dispose(); validationCts = null; CancelValidationButton.IsEnabled = false; }
    }
    string InfoReport(MediaInfo info)
    {
        var pretty = JsonSerializer.Serialize(JsonSerializer.Deserialize<JsonElement>(info.Json), new JsonSerializerOptions { WriteIndented = true });
        return $"File: {Path.GetFileName(info.Path)}\nPath: {info.Path}\nSize: {Format.Bytes(info.Size)} ({info.Size} bytes)\nDuration: {(info.Duration > 0 ? Format.Time(info.Duration) : "unknown")}\nContainer: {info.Container}\nAverage bitrate: {(info.Bitrate > 0 ? info.Bitrate + " bit/s" : "unknown")}\n\nFFprobe warnings:\n{(string.IsNullOrWhiteSpace(info.Warnings) ? "None reported" : info.Warnings)}\n\nAll available stream, chapter and metadata fields:\n{pretty}";
    }
    void RefreshInfo() => InfoText.Text = Selected is { } info ? InfoReport(info) : "Add and select a media file in a workspace to inspect it here.";
    void CopyInfo_Click(object sender, RoutedEventArgs e) { try { Clipboard.SetText(InfoText.Text); StatusText.Text = "Information copied."; } catch (Exception ex) { Error(ex); } }
    void ExportInfo_Click(object sender, RoutedEventArgs e)
    {
        if (Selected == null) return;
        var dialog = new SaveFileDialog { FileName = Path.GetFileNameWithoutExtension(Selected.Path) + "_info", Filter = "Text report|*.txt|FFprobe JSON|*.json" };
        if (dialog.ShowDialog(this) == true) { try { WriteNewText(dialog.FileName, dialog.FilterIndex == 2 ? Selected.Json : InfoText.Text); } catch (Exception ex) { Error(ex); } }
    }
    static void WriteNewText(string path, string text) { using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write); using var writer = new StreamWriter(stream, Encoding.UTF8); writer.Write(text); }
    async void Compare_Click(object sender, RoutedEventArgs e) => await Guard(async () =>
    {
        var job = jobs.LastOrDefault(j => j.State == JobState.Completed && j.Source.Path == Selected?.Path);
        if (job == null) throw new InvalidOperationException("Select a source with a completed job to compare.");
        var output = await RequireTools().ProbeAsync(job.Output, lifetime.Token);
        ShowText("Original / processed comparison", "ORIGINAL\n" + InfoReport(job.Source) + "\n\nPROCESSED\n" + InfoReport(output));
    });
    void LoadSettingsControls(AppSettings? preferences = null)
    {
        var s = preferences ?? settings;
        OutputFolderBox.Text = s.OutputDirectory; SuffixBox.Text = s.FilenameSuffix;
        AutoOpenBox.IsChecked = s.OpenOutput; ConfirmBox.IsChecked = s.ConfirmClose; RestoreBox.IsChecked = s.RestoreWindow; RememberBox.IsChecked = s.RememberPreferences;
        LightBox.IsChecked = s.LightTheme; Choose(AccelerationBox, s.Acceleration.ToString());
        ThreadsBox.Text = s.Threads.ToString(); ConcurrencyBox.Text = s.MaxJobs.ToString(); CacheLimitBox.Text = s.CacheMiB.ToString();
        LowPriorityBox.IsChecked = s.LowPriority; DetailedBox.IsChecked = s.DeepValidation; FfmpegBox.Text = s.FFmpegPath; FfprobeBox.Text = s.FFprobePath;
    }
    AppSettings ReadSettings()
    {
        var directory = OutputFolderBox.Text.Trim(); if (directory != "" && !Directory.Exists(directory)) throw new InvalidOperationException("Choose an existing output folder, or leave it blank.");
        return new()
        {
            OutputDirectory = directory, FilenameSuffix = SuffixBox.Text, OpenOutput = AutoOpenBox.IsChecked == true, ConfirmClose = ConfirmBox.IsChecked == true,
            RestoreWindow = RestoreBox.IsChecked == true, RememberPreferences = RememberBox.IsChecked == true, LightTheme = LightBox.IsChecked == true,
            Acceleration = Enum.Parse<Acceleration>(Text(AccelerationBox)), Threads = Integer(ThreadsBox.Text, "CPU threads", 1, 4), MaxJobs = Integer(ConcurrencyBox.Text, "Concurrent jobs", 1, 2),
            CacheMiB = Integer(CacheLimitBox.Text, "Thumbnail cache", 8, 512), LowPriority = LowPriorityBox.IsChecked == true, DeepValidation = DetailedBox.IsChecked == true,
            FFmpegPath = FfmpegBox.Text.Trim(), FFprobePath = FfprobeBox.Text.Trim(), WindowWidth = Width, WindowHeight = Height, LastOptions = RememberBox.IsChecked == true ? settings.LastOptions : null
        };
    }
    void ApplyTheme()
    {
        var palette = settings.LightTheme ? new Dictionary<string, string> { ["Bg"] = "#EFF3F6", ["Sidebar"] = "#E5EBEF", ["Panel"] = "#FFFFFF", ["Input"] = "#F4F7F9", ["Text"] = "#172536", ["Muted"] = "#53647A", ["Line"] = "#CBD5DF", ["Accent"] = "#30BA8A", ["Selection"] = "#DAF3E9", ["Warning"] = "#8A5C1B" }
          : new Dictionary<string, string> { ["Bg"] = "#0D1119", ["Sidebar"] = "#111722", ["Panel"] = "#171F2C", ["Input"] = "#101722", ["Text"] = "#E8EEF7", ["Muted"] = "#A5B2C7", ["Line"] = "#2A374A", ["Accent"] = "#6BE0B6", ["Selection"] = "#25433F", ["Warning"] = "#E4B876" };
        foreach (var pair in palette) Application.Current.Resources[pair.Key] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(pair.Value));
    }
    async void SaveSettings_Click(object sender, RoutedEventArgs e) => await Guard(async () =>
    {
        var proposed = ReadSettings();
        if (queue?.HasWork == true || importing || validating || configuring) throw new InvalidOperationException("Wait for active work to finish before saving settings.");
        var previous = settings; settings = proposed;
        try { await ConfigureToolsAsync(); store.Save(settings); ApplyTheme(); RefreshStorage(); StatusText.Text = "Settings saved."; }
        catch { settings = previous; throw; }
    });
    async void VerifyTools_Click(object sender, RoutedEventArgs e) => await Guard(ConfigureToolsAsync);
    async void TestGpu_Click(object sender, RoutedEventArgs e) => await Guard(async () =>
    {
        if (queue?.HasWork == true || configuring || importing || validating) throw new InvalidOperationException("Wait for queued jobs and tool verification to finish before the GPU test.");
        configuring = true;
        try { var service = RequireTools(); StatusText.Text = "Testing real H.264 Quick Sync encoding…"; await service.TestGpuAsync(lifetime.Token); ToolReport.Text = service.Version + "\n" + service.GpuReport; ToolBadge.Text = service.QsvAvailable ? "FFmpeg ready · Intel QSV" : "FFmpeg ready · CPU"; StatusText.Text = "GPU test finished."; UpdatePlan(); }
        finally { configuring = false; }
    });
    void ShowEncoders_Click(object sender, RoutedEventArgs e) { if (tools != null) ShowText("FFmpeg encoders", tools.Encoders + "\n\nEncoder listing describes this FFmpeg build. Only the four exposed video codecs are used by Luma. Hardware availability is checked by an actual encoding test."); }
    void BrowseTool_Click(object sender, RoutedEventArgs e)
    {
        var tag = ((Button)sender).Tag?.ToString(); var dialog = new OpenFileDialog { Filter = "Windows executable|*.exe", Title = "Choose existing " + tag + ".exe" };
        if (dialog.ShowDialog(this) == true) { if (tag == "ffmpeg") FfmpegBox.Text = dialog.FileName; else FfprobeBox.Text = dialog.FileName; }
    }
    void BrowseOutput_Click(object sender, RoutedEventArgs e) { var dialog = new OpenFolderDialog { Title = "Default output folder" }; if (dialog.ShowDialog(this) == true) OutputFolderBox.Text = dialog.FolderName; }
    void RefreshStorage()
    {
        var size = Directory.Exists(store.CacheDirectory) ? new DirectoryInfo(store.CacheDirectory).EnumerateFiles().Sum(f => f.Length) : 0;
        StorageText.Text = $"Data: {store.DataDirectory}\nThumbnail cache: {store.CacheDirectory}\nCache size: {Format.Bytes(size)}\nLogs rotate at 2 MiB per day and are kept for 7 days.";
    }
    void ClearCache_Click(object sender, RoutedEventArgs e) { try { thumbnailCts?.Cancel(); MediaTools.TrimCache(store.CacheDirectory, 0); RefreshStorage(); StatusText.Text = "Thumbnail cache cleared."; } catch (Exception ex) { Error(ex); } }
    void OpenLogs_Click(object sender, RoutedEventArgs e) { Directory.CreateDirectory(DiagnosticLog.DirectoryPath); OpenPath(DiagnosticLog.DirectoryPath); }
    void CleanLogs_Click(object sender, RoutedEventArgs e) { try { DiagnosticLog.Cleanup(); StatusText.Text = "Old logs cleared."; } catch (Exception ex) { Error(ex); } }
    void ExportSettings_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Filter = "JSON settings|*.json", FileName = "luma-settings.json" };
        if (dialog.ShowDialog(this) == true) { try { if (File.Exists(dialog.FileName)) throw new IOException("Choose a new filename to preserve the existing file."); store.Export(dialog.FileName, ReadSettings()); } catch (Exception ex) { Error(ex); } }
    }
    void ImportSettings_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "JSON settings|*.json" };
        if (dialog.ShowDialog(this) == true) { try { if (queue?.HasWork == true || configuring) throw new InvalidOperationException("Wait for active jobs to finish."); var imported = store.Import(dialog.FileName); LoadSettingsControls(imported); StatusText.Text = "Imported settings loaded for review. Click Save settings to verify tools and persist."; } catch (Exception ex) { Error(ex); } }
    }
    void ResetSettings_Click(object sender, RoutedEventArgs e)
    {
        if (queue?.HasWork == true || configuring) { StatusText.Text = "Wait for active jobs to finish."; return; }
        if (MessageBox.Show(this, "Reset application preferences? Videos, outputs and logs are kept.", "Reset settings", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        settings = new(); LoadSettingsControls(); ApplyTheme(); try { store.Save(settings); StatusText.Text = "Defaults saved. Verify tools if changing the installation paths."; } catch (Exception ex) { Error(ex); }
    }
    async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (allowClose) return;
        e.Cancel = true; if (closing) return;
        if ((queue?.HasWork == true || importing || validating) && settings.ConfirmClose && MessageBox.Show(this, "Cancel active work and close? Original files are kept; unfinished outputs are removed.", "Close Luma", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        closing = true; IsEnabled = false; lifetime.Cancel(); thumbnailCts?.Cancel(); validationCts?.Cancel();
        try
        {
            timer.Stop(); await StopPlayerAsync();
            if (queue != null) await queue.DisposeAsync();
            VideoView.MediaPlayer = null; player?.Dispose(); vlc?.Dispose();
            settings.WindowWidth = RestoreBounds.Width; settings.WindowHeight = RestoreBounds.Height; store.Save(settings);
        }
        catch (Exception error) { DiagnosticLog.Write("Shutdown: " + error); }
        finally { DiagnosticLog.LogFailure -= OnLogFailure; allowClose = true; Close(); }
    }
}
