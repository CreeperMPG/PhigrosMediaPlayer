using PhigrosMediaPlayer.Models;
using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shell;
using Microsoft.Win32;
using NAudio.Wave;
using NAudio.Vorbis;
using Newtonsoft.Json;
using System.Linq;
using System.Windows.Threading;
using System.Collections.Generic;
using PhigrosMediaPlayer.Dialogs;
using System.Threading.Tasks;

//
// 修改说明：
// - 在播放列表中导入 ZIP/PEZ 时，会记录已解压临时文件与原始压缩包路径和条目的映射 (extractedToArchive)。
// - 保存播放列表时，如果条目来自压缩包，则在导出的 JSON 中保存压缩包路径和条目名（Archive/EntryName），而不是临时音频文件路径。
// - 加载播放列表时，支持两种格式：
//    1) 新格式（包含 Archive/EntryName）：当 Path 指向 ZIP/PEZ（或 Archive 字段存在）时，会重新从压缩包解压指定条目到临时目录并加入播放列表。
//    2) 老格式（仅 Title/Path，对应 Track[]）：尝试直接按路径加入（如果文件存在）；不存在则提示并跳过。
// - 新增辅助 DTO `PlaylistEntryDto` 与方法 `ExtractEntryFromArchive`，并维护 `extractedToArchive` 映射以保持后续保存的正确性。
// - 保持原有功能不变，尽量向后兼容。
//


namespace PhigrosMediaPlayer
{
    /// <summary>
    /// MainWindow.xaml 的交互逻辑
    /// </summary>
    public partial class MainWindow : Window
    {
        private ObservableCollection<Track> Playlist = new ObservableCollection<Track>();
        private int currentIndex = -1;

        private ThumbButtonInfo tbPrev;
        private ThumbButtonInfo tbPlayPause;
        private ThumbButtonInfo tbNext;

        // NAudio playback fields
        private IWavePlayer waveOut;
        private WaveStream audioFile;

        // Progress timer and slider state
        private DispatcherTimer positionTimer;
        private bool isDraggingSlider = false;

        // 映射：已解压的临时文件路径 -> (原始压缩包路径, 压缩包内条目 FullName)
        private Dictionary<string, Tuple<string, string>> extractedToArchive = new Dictionary<string, Tuple<string, string>>(StringComparer.OrdinalIgnoreCase);

        public MainWindow()
        {
            InitializeComponent();

            ListBoxPlaylist.ItemsSource = Playlist;
            InitializeTaskbarButtons();

            positionTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            positionTimer.Tick += PositionTimer_Tick;

            this.Closing += MainWindow_Closing;

            UpdateUiState();
        }

        private void MainWindow_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            positionTimer.Stop();
            StopAndDisposeAudio();
        }

        private void PositionTimer_Tick(object sender, EventArgs e)
        {
            if (audioFile == null || isDraggingSlider) return;

            try
            {
                var wf = audioFile.WaveFormat;
                double totalSeconds = 0;
                double currentSeconds = 0;
                if (audioFile.Length > 0 && wf.AverageBytesPerSecond > 0)
                {
                    totalSeconds = audioFile.Length / (double)wf.AverageBytesPerSecond;
                    currentSeconds = audioFile.Position / (double)wf.AverageBytesPerSecond;
                }

                SliderProgress.Maximum = Math.Max(1, totalSeconds);
                SliderProgress.Value = Math.Min(SliderProgress.Maximum, currentSeconds);
                TxtTime.Text = FormatTime(currentSeconds) + " / " + FormatTime(totalSeconds);
            }
            catch { }
        }

        private string FormatTime(double seconds)
        {
            if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds <= 0) return "0:00";
            var ts = TimeSpan.FromSeconds(seconds);
            if (ts.Hours > 0)
                return string.Format("{0}:{1:D2}:{2:D2}", (int)ts.TotalHours, ts.Minutes, ts.Seconds);
            else
                return string.Format("{0}:{1:D2}", ts.Minutes, ts.Seconds);
        }

        private void InitializeTaskbarButtons()
        {
            // 在 Taskbar 中创建缩略图按钮（上一首 / 播放/暂停 / 下一首）
            tbPrev = new ThumbButtonInfo { Description = "上一首" };
            tbPrev.Click += TbPrev_Click;

            tbPlayPause = new ThumbButtonInfo { Description = "播放/暂停" };
            tbPlayPause.Click += TbPlayPause_Click;

            tbNext = new ThumbButtonInfo { Description = "下一首" };
            tbNext.Click += TbNext_Click;

            // 使用简单的矢量图形生成图标
            tbPrev.ImageSource = CreatePrevIcon(24, Brushes.Black);
            tbPlayPause.ImageSource = CreatePlayIcon(24, Brushes.Black);
            tbNext.ImageSource = CreateNextIcon(24, Brushes.Black);

            TaskbarInfo.ThumbButtonInfos.Add(tbPrev);
            TaskbarInfo.ThumbButtonInfos.Add(tbPlayPause);
            TaskbarInfo.ThumbButtonInfos.Add(tbNext);
        }

        private ImageSource CreatePlayIcon(double size, Brush brush)
        {
            var geo = Geometry.Parse("M0,0 L0,16 L12,8 Z");
            var drawing = new GeometryDrawing(brush, null, geo);
            var group = new DrawingGroup();
            group.Children.Add(drawing);
            group.Transform = new ScaleTransform(size / 16.0, size / 16.0);
            return new DrawingImage(group);
        }

        private ImageSource CreatePauseIcon(double size, Brush brush)
        {
            // 两个矩形
            var g = new GeometryGroup();
            g.Children.Add(new RectangleGeometry(new Rect(0, 0, 4, 16)));
            g.Children.Add(new RectangleGeometry(new Rect(8, 0, 4, 16)));
            var drawing = new GeometryDrawing(brush, null, g);
            var group = new DrawingGroup();
            group.Children.Add(drawing);
            group.Transform = new ScaleTransform(size / 16.0, size / 16.0);
            return new DrawingImage(group);
        }

        private ImageSource CreatePrevIcon(double size, Brush brush)
        {
            // |<< 图标：竖线 + 三角
            var g = new GeometryGroup();
            g.Children.Add(new RectangleGeometry(new Rect(0, 0, 2, 16)));
            g.Children.Add(Geometry.Parse("M4,0 L4,16 L12,8 Z"));
            var drawing = new GeometryDrawing(brush, null, g);
            var group = new DrawingGroup();
            group.Children.Add(drawing);
            group.Transform = new ScaleTransform(size / 16.0, size / 16.0);
            return new DrawingImage(group);
        }

        private ImageSource CreateNextIcon(double size, Brush brush)
        {
            // >>| （镜像）
            var g = new GeometryGroup();
            g.Children.Add(new RectangleGeometry(new Rect(12, 0, 2, 16)));
            g.Children.Add(Geometry.Parse("M4,8 L12,0 L12,16 Z"));
            var drawing = new GeometryDrawing(brush, null, g);
            var group = new DrawingGroup();
            group.Children.Add(drawing);
            group.Transform = new ScaleTransform(size / 16.0, size / 16.0);
            return new DrawingImage(group);
        }

        private void BtnAdd_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Multiselect = true,
                Filter = "音频文件|*.mp3;*.wav;*.wma;*.aac;*.flac;*.ogg|所有文件|*.*"
            };
            if (dlg.ShowDialog() == true)
            {
                foreach (var f in dlg.FileNames)
                {
                    Playlist.Add(new Track { Title = System.IO.Path.GetFileNameWithoutExtension(f), Path = f });
                }
                if (currentIndex == -1 && Playlist.Count > 0)
                {
                    PlayAt(0);
                }
            }
            UpdateUiState();
        }
        private void BtnAddZIPPEZ_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Multiselect = true,
                Filter = "Phigros 谱面文件|*.zip;*.pez|所有文件|*.*"
            };
            if (dlg.ShowDialog() != true) return;
            ProgressDialog dialog = new ProgressDialog("正在导入 PEZ 文件，请稍候...");
            dialog.ShowAsync(this);
            Task.Run(() =>
            {
                for (int fileIndex = 0; fileIndex < dlg.FileNames.Length; fileIndex++)
                {
                    string archivePath = dlg.FileNames[fileIndex];
                    try
                    {
                        using (var fs = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                        using (var za = new ZipArchive(fs, ZipArchiveMode.Read))
                        {
                            // 找到 info.txt（忽略大小写和路径）
                            var infoEntry = za.Entries.FirstOrDefault(zaEntry =>
                                string.Equals(Path.GetFileName(zaEntry.FullName), "info.txt", StringComparison.OrdinalIgnoreCase));

                            if (infoEntry == null)
                            {
                                // 跳过没有 info.txt 的压缩包
                                continue;
                            }

                            string infoText;
                            using (var sr = new StreamReader(infoEntry.Open()))
                            {
                                infoText = sr.ReadToEnd();
                            }

                            // 解析 info.txt 中的键值（至少需要 Name 和 Song）
                            var parsed = ParseInfoText(infoText);
                            string nameValue;
                            string songValue;
                            parsed.TryGetValue("Name", out nameValue);
                            parsed.TryGetValue("Song", out songValue);

                            if (string.IsNullOrWhiteSpace(nameValue) || string.IsNullOrWhiteSpace(songValue))
                            {
                                // 必须同时有 Name 和 Song，否则跳过
                                continue;
                            }

                            // 在压缩包中查找与 Song 匹配的条目（匹配文件名或完整路径）
                            ZipArchiveEntry songEntry = za.Entries
                                .FirstOrDefault(zaEntry =>
                                    string.Equals(zaEntry.FullName, songValue, StringComparison.OrdinalIgnoreCase)
                                    || string.Equals(Path.GetFileName(zaEntry.FullName), songValue, StringComparison.OrdinalIgnoreCase));

                            if (songEntry == null)
                            {
                                // 未找到歌曲文件，跳过
                                continue;
                            }

                            // 将歌曲提取到临时目录（每个压缩包使用一个唯一子目录，避免冲突）
                            var tempDir = Path.Combine(Path.GetTempPath(), "PhigrosMediaPlayer", Guid.NewGuid().ToString("N"));
                            Directory.CreateDirectory(tempDir);
                            var outPath = Path.Combine(tempDir, Path.GetFileName(songEntry.FullName));

                            // 使用流复制来提取（避免对 System.IO.Compression.FileSystem 的额外引用）
                            using (var entryStream = songEntry.Open())
                            using (var outFs = new FileStream(outPath, FileMode.Create, FileAccess.Write, FileShare.None))
                            {
                                entryStream.CopyTo(outFs);
                            }

                            Dispatcher.Invoke(() =>
                            {
                                // 将歌曲添加到播放列表，使用 info 中的 Name 作为标题
                                Playlist.Add(new Track { Title = nameValue.Trim(), Path = outPath });
                            });

                            // 记录映射：临时解压文件 -> (archivePath, entryFullName)
                            extractedToArchive[outPath] = Tuple.Create(archivePath, songEntry.FullName);
                            dialog.SetProgress(fileIndex + 1, dlg.FileNames.Length);
                        }
                    }
                    catch (Exception ex)
                    {
                        // 对单个压缩包出错不影响其它压缩包的处理；记录或提示简短错误
                        MessageBox.Show($"处理压缩包失败: {Path.GetFileName(archivePath)}\n{ex.Message}");
                    }
                }
                Dispatcher.Invoke(() =>
                {
                    dialog.Hide();
                    if (currentIndex == -1 && Playlist.Count > 0)
                    {
                        PlayAt(0);
                    }
                    UpdateUiState();
                });
            });
        }

        /// <summary>
        /// 解析 info.txt 的简单键值，返回键(不区分大小写)->值的字典。
        /// 支持类似 "Name: value Song: value2 Picture: ..." 的单行或多行格式。
        /// </summary>
        private static System.Collections.Generic.Dictionary<string, string> ParseInfoText(string text)
        {
            var keys = new[] { "Name", "Song", "Picture", "Chart", "Level", "Composer", "Illustrator", "Charter" };
            var dict = new System.Collections.Generic.Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(text)) return dict;

            // 我们查找每个 key: 起始位置，然后到下一个 key: 的位置之间作为值
            for (int i = 0; i < keys.Length; i++)
            {
                var key = keys[i];
                var keyPattern = key + ":";
                var start = text.IndexOf(keyPattern, StringComparison.OrdinalIgnoreCase);
                if (start < 0) continue;
                start += keyPattern.Length;

                int nextPos = -1;
                for (int j = 0; j < keys.Length; j++)
                {
                    if (j == i) continue;
                    var other = keys[j] + ":";
                    var pos = text.IndexOf(other, start, StringComparison.OrdinalIgnoreCase);
                    if (pos >= 0 && (nextPos < 0 || pos < nextPos))
                        nextPos = pos;
                }

                string value;
                if (nextPos >= 0)
                    value = text.Substring(start, nextPos - start);
                else
                    value = text.Substring(start);

                value = value.Trim();
                if (!dict.ContainsKey(key) && !string.IsNullOrWhiteSpace(value))
                {
                    dict[key] = value;
                }
            }

            return dict;
        }

        private void PlayAt(int index)
        {
            if (index < 0 || index >= Playlist.Count) return;
            currentIndex = index;
            var t = Playlist[index];
            ListBoxPlaylist.SelectedIndex = index;
            try
            {
                StopAndDisposeAudio();

                var ext = System.IO.Path.GetExtension(t.Path).ToLowerInvariant();
                if (ext == ".ogg")
                {
                    audioFile = new VorbisWaveReader(t.Path);
                }
                else
                {
                    // AudioFileReader supports mp3/wav/aac (if supported by media foundation)
                    audioFile = new AudioFileReader(t.Path);
                }

                waveOut = new WaveOutEvent();
                waveOut.Init(audioFile);
                waveOut.PlaybackStopped += WaveOut_PlaybackStopped;
                waveOut.Play();

                BtnPlayPause.Content = "暂停";
                TxtNow.Text = $"正在播放: {t.Title}";
                UpdateTaskbarPlayIcon(isPlaying: true);
                UpdateSystemMediaMetadata(t);

                // start position updates
                positionTimer.Start();
            }
            catch (Exception ex)
            {
                MessageBox.Show("无法播放文件: " + ex.Message);
            }
            UpdateUiState();
        }

        private void WaveOut_PlaybackStopped(object sender, StoppedEventArgs e)
        {
            // 如果是自然播放结束，则跳到下一首
            bool naturalEnd = false;
            try
            {
                if (audioFile != null && audioFile.Length > 0 && audioFile.Position >= audioFile.Length)
                    naturalEnd = true;
            }
            catch { }

            // Dispose here and then if natural end, advance
            // Use Dispatcher to ensure UI thread
            Dispatcher.Invoke(() =>
            {
                positionTimer.Stop();
                StopAndDisposeAudio();
                if (naturalEnd)
                    PlayNext();
                else
                {
                    // playback stopped by user or error
                    BtnPlayPause.Content = "播放";
                    UpdateTaskbarPlayIcon(false);
                    UpdateUiState();
                }
            });
        }

        private void StopAndDisposeAudio()
        {
            if (waveOut != null)
            {
                waveOut.PlaybackStopped -= WaveOut_PlaybackStopped;
                try { waveOut.Stop(); } catch { }
                waveOut.Dispose();
                waveOut = null;
            }
            if (audioFile != null)
            {
                try { audioFile.Dispose(); } catch { }
                audioFile = null;
            }
        }

        private void BtnPlayPause_Click(object sender, RoutedEventArgs e)
        {
            TogglePlayPause();
        }

        private void TogglePlayPause()
        {
            if (waveOut == null)
            {
                if (Playlist.Count > 0) PlayAt(0);
                return;
            }

            if (waveOut.PlaybackState == PlaybackState.Playing)
            {
                waveOut.Pause();
                BtnPlayPause.Content = "播放";
                UpdateTaskbarPlayIcon(false);
                positionTimer.Stop();
            }
            else
            {
                waveOut.Play();
                BtnPlayPause.Content = "暂停";
                UpdateTaskbarPlayIcon(true);
                positionTimer.Start();
            }
            UpdateUiState();
        }

        private void BtnPrev_Click(object sender, RoutedEventArgs e)
        {
            PlayPrevious();
        }

        private void BtnNext_Click(object sender, RoutedEventArgs e)
        {
            PlayNext();
        }

        private void PlayPrevious()
        {
            if (Playlist.Count == 0) return;
            var idx = currentIndex - 1;
            if (idx < 0) idx = Playlist.Count - 1;
            PlayAt(idx);
        }

        private void PlayNext()
        {
            if (Playlist.Count == 0) return;
            var idx = currentIndex + 1;
            if (idx >= Playlist.Count) idx = 0;
            PlayAt(idx);
        }
        private void UpdateUiState()
        {
            BtnPrev.IsEnabled = Playlist.Count > 0;
            BtnNext.IsEnabled = Playlist.Count > 0;
            BtnPlayPause.IsEnabled = Playlist.Count > 0 || waveOut != null;

            tbPrev.IsEnabled = BtnPrev.IsEnabled;
            tbNext.IsEnabled = BtnNext.IsEnabled;
            tbPlayPause.IsEnabled = BtnPlayPause.IsEnabled;
        }

        // 保存播放列表为 JSON
        private void BtnSaveList_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new SaveFileDialog
            {
                Filter = "播放列表 (JSON)|*.json",
                DefaultExt = "json",
                FileName = "playlist.json"
            };
            if (dlg.ShowDialog() == true)
            {
                try
                {
                    // 使用 DTO 保存额外信息（当条目来自压缩包时保存 Archive 和 EntryName）
                    var arr = new List<PlaylistEntryDto>();
                    foreach (var p in Playlist)
                    {
                        PlaylistEntryDto dto = new PlaylistEntryDto
                        {
                            Title = p.Title,
                            Path = p.Path,
                            Archive = null,
                            EntryName = null
                        };

                        // 如果该播放项是从压缩包解压出来的临时文件，则将 Archive 存为压缩包路径，并记录 EntryName
                        if (extractedToArchive.TryGetValue(p.Path, out var tup))
                        {
                            dto.Path = tup.Item1; // archive path
                            dto.Archive = tup.Item1;
                            dto.EntryName = tup.Item2;
                        }

                        arr.Add(dto);
                    }

                    var json = JsonConvert.SerializeObject(arr, Formatting.Indented);
                    File.WriteAllText(dlg.FileName, json);
                }
                catch (Exception ex)
                {
                    MessageBox.Show("保存播放列表失败: " + ex.Message);
                }
            }
        }

        // 加载播放列表从 JSON
        private void BtnLoadList_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Filter = "播放列表 (JSON)|*.json",
                Multiselect = false
            };
            if (dlg.ShowDialog() == true)
            {
                try
                {
                    var json = File.ReadAllText(dlg.FileName);

                    // 先尝试按新 DTO 格式反序列化
                    Playlist.Clear();

                    PlaylistEntryDto[] dtoArr = null;
                    try
                    {
                        dtoArr = JsonConvert.DeserializeObject<PlaylistEntryDto[]>(json);
                    }
                    catch
                    {
                        dtoArr = null;
                    }

                    if (dtoArr != null && dtoArr.Length > 0 && (dtoArr.Any(d => d.Archive != null || d.EntryName != null || d.Path != null)))
                    {

                        ProgressDialog dialog = new ProgressDialog("正在导入播放列表，请稍候...");
                        Task.Run(() =>
                        {
                            for (int dtoIndex = 0; dtoIndex < dtoArr.Length; dtoIndex++)
                            {
                                PlaylistEntryDto dto = dtoArr[dtoIndex];
                                // 如果 DTO 明确指示 Archive 或 Path 是 zip/pez，则尝试重新解压
                                var possibleArchivePath = dto.Archive ?? dto.Path;
                                var ext = possibleArchivePath != null ? Path.GetExtension(possibleArchivePath).ToLowerInvariant() : string.Empty;
                                if (!string.IsNullOrWhiteSpace(possibleArchivePath) &&
                                    (ext == ".zip" || ext == ".pez") &&
                                    File.Exists(possibleArchivePath) &&
                                    !string.IsNullOrWhiteSpace(dto.EntryName))
                                {
                                    // 从 Archive 解压 EntryName
                                    var extracted = ExtractEntryFromArchive(possibleArchivePath, dto.EntryName);
                                    if (!string.IsNullOrEmpty(extracted))
                                    {
                                        Dispatcher.Invoke(() =>
                                        {
                                            Playlist.Add(new Track { Title = dto.Title ?? System.IO.Path.GetFileNameWithoutExtension(extracted), Path = extracted });
                                        });
                                        continue;
                                    }
                                    else
                                    {
                                        MessageBox.Show($"无法从压缩包解压: {possibleArchivePath} -> {dto.EntryName}");
                                        continue;
                                    }
                                }

                                // 否则将 Path 当作普通文件路径处理（如果存在）
                                if (!string.IsNullOrWhiteSpace(dto.Path) && File.Exists(dto.Path))
                                {
                                    Dispatcher.Invoke(() =>
                                    {
                                        Playlist.Add(new Track { Title = dto.Title ?? System.IO.Path.GetFileNameWithoutExtension(dto.Path), Path = dto.Path });
                                    }); 
                                    continue;
                                }

                                // 如果 Archive 指定但文件不存在，提示并跳过
                                if (!string.IsNullOrWhiteSpace(possibleArchivePath) && (ext == ".zip" || ext == ".pez") && !File.Exists(possibleArchivePath))
                                {
                                    MessageBox.Show($"压缩包未找到: {possibleArchivePath}（条目: {dto.EntryName}）");
                                    continue;
                                }
                                dialog.SetProgress(dtoIndex + 1, dtoArr.Length);
                                // 否则如果 Path 存在但无法处理，跳过
                            }
                            if (Playlist.Count > 0)
                                PlayAt(0);
                        });

                    }
                    else
                    {
                        // 兼容旧格式（直接是 Track[]）
                        try
                        {
                            var oldArr = JsonConvert.DeserializeObject<Track[]>(json);
                            if (oldArr != null)
                            {
                                foreach (var t in oldArr)
                                {
                                    if (!string.IsNullOrWhiteSpace(t.Path) && File.Exists(t.Path))
                                    {
                                        Playlist.Add(new Track { Title = t.Title, Path = t.Path });
                                    }
                                    else
                                    {
                                        // 文件不存在则提示（老版本可能保存了临时解压路径）
                                        MessageBox.Show($"文件未找到: {t.Path}");
                                    }
                                }

                                if (Playlist.Count > 0)
                                    PlayAt(0);
                            }
                        }
                        catch (Exception ex2)
                        {
                            MessageBox.Show("加载播放列表失败: " + ex2.Message);
                        }
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show("加载播放列表失败: " + ex.Message);
                }
            }
            UpdateUiState();
        }

        // DTO 用于保存附加信息（Archive, EntryName）
        private class PlaylistEntryDto
        {
            public string Title { get; set; }
            public string Path { get; set; }
            public string Archive { get; set; }
            public string EntryName { get; set; }
        }

        /// <summary>
        /// 从指定压缩包中解压条目（entryName 可以是 FullName 或仅文件名），
        /// 返回解压后的临时文件路径（同时在 extractedToArchive 中注册映射），失败返回 null。
        /// </summary>
        private string ExtractEntryFromArchive(string archivePath, string entryName)
        {
            try
            {
                using (var fs = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var za = new ZipArchive(fs, ZipArchiveMode.Read))
                {
                    var songEntry = za.Entries.FirstOrDefault(e =>
                        string.Equals(e.FullName, entryName, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(Path.GetFileName(e.FullName), entryName, StringComparison.OrdinalIgnoreCase));

                    if (songEntry == null) return null;

                    var tempDir = Path.Combine(Path.GetTempPath(), "PhigrosMediaPlayer", Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(tempDir);
                    var outPath = Path.Combine(tempDir, Path.GetFileName(songEntry.FullName));

                    using (var entryStream = songEntry.Open())
                    using (var outFs = new FileStream(outPath, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        entryStream.CopyTo(outFs);
                    }

                    extractedToArchive[outPath] = Tuple.Create(archivePath, songEntry.FullName);
                    return outPath;
                }
            }
            catch
            {
                return null;
            }
        }

        // 更新系统媒体元数据（基础实现）
        private void UpdateSystemMediaMetadata(Track t)
        {
            // 最简单的做法：将窗口标题更新为当前曲目，这会显示在任务栏和部分系统 UI 的缩略图。
            // 完整的 SystemMediaTransportControls (SMTC) 集成需要引用 Windows Runtime (Windows.winmd) 并使用 Windows.Media APIs。
            // 如果你希望我添加 SMTC 支持，我可以在项目中添加相应引用并实现更完整的集成（会需要修改项目文件）。

            this.Title = $"Phigros Media Player - {t.Title}";

            //作为额外提示，设置 Taskbar overlay tooltip via TaskbarItemInfo is not available, but Title is usually enough。

            // TODO: If album art available, set TaskbarInfo.Overlay to an ImageSource representing artwork.
        }

        private void UpdateTaskbarPlayIcon(bool isPlaying)
        {
            tbPlayPause.ImageSource = isPlaying ? CreatePauseIcon(24, Brushes.Black) : CreatePlayIcon(24, Brushes.Black);
            // 更新按钮描述（可选）
            tbPlayPause.Description = isPlaying ? "暂停" : "播放";
        }

        // 缩略图按钮回调 (Taskbar)
        private void TbPrev_Click(object sender, EventArgs e) => PlayPrevious();
        private void TbPlayPause_Click(object sender, EventArgs e) => TogglePlayPause();
        private void TbNext_Click(object sender, EventArgs e) => PlayNext();

        // Slider events for seeking
        private void SliderProgress_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            isDraggingSlider = true;
            positionTimer.Stop();
        }

        private void SliderProgress_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            isDraggingSlider = false;
            if (audioFile != null)
            {
                var wf = audioFile.WaveFormat;
                double seconds = SliderProgress.Value;
                long pos = (long)(seconds * wf.AverageBytesPerSecond);
                if (wf.BlockAlign > 0)
                {
                    pos -= pos % wf.BlockAlign;
                }
                pos = Math.Max(0, Math.Min(pos, audioFile.Length));
                try { audioFile.Position = pos; } catch { }
            }

            if (waveOut != null && waveOut.PlaybackState == PlaybackState.Playing)
                positionTimer.Start();
        }

        private void SliderProgress_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (isDraggingSlider)
            {
                TxtTime.Text = FormatTime(SliderProgress.Value) + " / " + FormatTime(SliderProgress.Maximum);
            }
        }

        private void ListBoxPlaylist_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (ListBoxPlaylist.SelectedIndex >= 0)
            {
                PlayAt(ListBoxPlaylist.SelectedIndex);
            }
        }

        //右键菜单删除处理
        private void MenuDelete_Click(object sender, RoutedEventArgs e)
        {

            int idx = ListBoxPlaylist.SelectedIndex;
            if (idx < 0) return;

            // 如果删除当前正在播放的曲目
            bool wasCurrent = (idx == currentIndex);

            // 若删除的是已解压的临时文件，尝试移除映射（不删除临时文件本身）
            var removedPath = Playlist[idx].Path;
            if (extractedToArchive.ContainsKey(removedPath))
            {
                extractedToArchive.Remove(removedPath);
            }

            Playlist.RemoveAt(idx);

            if (wasCurrent)
            {
                // 停止播放并尝试播放下一首（如果有）
                StopAndDisposeAudio();
                if (Playlist.Count > 0)
                {
                    var nextIdx = idx;
                    if (nextIdx >= Playlist.Count) nextIdx = 0;
                    PlayAt(nextIdx);
                }
                else
                {
                    currentIndex = -1;
                    BtnPlayPause.Content = "播放";
                    UpdateTaskbarPlayIcon(false);
                }
            }
            else
            {
                // 如果删除项位于当前索引之前，需要调整 currentIndex
                if (idx < currentIndex)
                    currentIndex--;
            }

            UpdateUiState();
        }

        private void ListBoxPlaylist_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            // Select the item under the mouse so ContextMenu actions operate on it
            var lb = sender as ListBox;
            if (lb == null) return;
            var pt = e.GetPosition(lb);
            var element = lb.InputHitTest(pt) as DependencyObject;
            while (element != null && !(element is ListBoxItem))
                element = VisualTreeHelper.GetParent(element);
            if (element is ListBoxItem lbi)
            {
                lbi.IsSelected = true;
            }
        }
        public void BtnAbout_Click(object sender, RoutedEventArgs e)
        {
            new AboutWindow().Show();
        }
    }
}