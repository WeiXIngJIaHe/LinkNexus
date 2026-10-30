using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using LinkNexus.Blueprint.Core;

namespace LinkNexus.Blueprint.Nodes
{
    /// <summary>
    /// CSV 流式落盘保存卡片 (CsvSaveNode)
    /// 实时将多通道浮点波形或遥测帧附带高精度毫秒时间戳流式写入本地 UTF-8 CSV 数据表
    /// </summary>
    public sealed class CsvSaveNode : NodeBase
    {
        private const string IconSvg = "M6,3 H15 L19,7 V20 A1,1 0 0,1 18,21 H6 A1,1 0 0,1 5,20 V4 A1,1 0 0,1 6,3 Z M9,3 V8 H15 V3 M8,13 H16 M8,17 H14";

        private readonly NodeParameter _prefixParam;
        private readonly NodeParameter _recordToggleParam;
        private readonly NodeParameter _openFolderBtnParam;
        private readonly NodeParameter _statsBadgeParam;

        private readonly object _fileLock = new();
        private StreamWriter? _writer;
        private Stopwatch? _stopwatch;
        private bool _headerWritten;
        private long _savedRows;
        private string _currentFileName = string.Empty;

        public override string NodeTypeKey => "csv_save";
        public string SaveDirectory { get; }

        public CsvSaveNode(double x = 760, double y = 48)
            : base("CSV 数据落盘", "持久化存储", "#F59E0B", IconSvg, x, y)
        {
            AddInputPin("录制数据流", PinDataType.Any);

            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            SaveDirectory = Path.Combine(desktop, "LinkNexus_Captures");

            _prefixParam = new NodeParameter("prefix", "文件前缀", NodeParamKind.Text, "linknexus_capture");

            _recordToggleParam = new NodeParameter("record", "流式刷盘", NodeParamKind.Toggle)
            {
                BoolChangedCallback = recording =>
                {
                    if (recording) StartRecording();
                    else StopRecording();
                }
            };

            _openFolderBtnParam = new NodeParameter("open_dir", "归档目录", NodeParamKind.ActionButton, "打开 CSV 保存文件夹")
            {
                ActionCommand = new RelayCommand(OpenCaptureDirectory)
            };

            _statsBadgeParam = new NodeParameter("stats", "落盘统计", NodeParamKind.ReadOnlyBadge, "未开启录制 · Desktop\\LinkNexus_Captures");

            Parameters.Add(_prefixParam);
            Parameters.Add(_recordToggleParam);
            Parameters.Add(_openFolderBtnParam);
            Parameters.Add(_statsBadgeParam);
        }

        private void StartRecording()
        {
            lock (_fileLock)
            {
                StopRecordingCore();
                try
                {
                    Directory.CreateDirectory(SaveDirectory);
                    string safePrefix = string.IsNullOrWhiteSpace(_prefixParam.Value) ? "capture" : _prefixParam.Value.Trim();
                    foreach (char invalid in Path.GetInvalidFileNameChars())
                    {
                        safePrefix = safePrefix.Replace(invalid, '_');
                    }

                    _currentFileName = $"{safePrefix}_{DateTime.Now:yyyyMMdd_HHmmss}.csv";
                    string fullPath = Path.Combine(SaveDirectory, _currentFileName);

                    _writer = new StreamWriter(new FileStream(fullPath, FileMode.Create, FileAccess.Write, FileShare.Read), new UTF8Encoding(true), 16384);
                    _stopwatch = Stopwatch.StartNew();
                    _headerWritten = false;
                    _savedRows = 0;

                    UpdateStatusOnUi("正在写盘", true);
                    UpdateStatsBadge($"录制中: {_currentFileName} (0 行)");
                }
                catch (Exception ex)
                {
                    UpdateStatusOnUi("写盘异常", false);
                    UpdateStatsBadge($"创建失败: {ex.Message}");
                    Application.Current?.Dispatcher.BeginInvoke(() => _recordToggleParam.SetBoolSilently(false));
                }
            }
        }

        private void StopRecording()
        {
            long finalRows;
            string name;
            lock (_fileLock)
            {
                finalRows = _savedRows;
                name = _currentFileName;
                StopRecordingCore();
            }

            UpdateStatusOnUi("已封存", false);
            if (!string.IsNullOrEmpty(name))
            {
                UpdateStatsBadge($"已保存 {finalRows} 行 -> {name}");
            }
            else
            {
                UpdateStatsBadge("录制已停止");
            }
        }

        private void StopRecordingCore()
        {
            if (_writer != null)
            {
                try
                {
                    _writer.Flush();
                    _writer.Close();
                }
                catch { }
                _writer.Dispose();
                _writer = null;
            }
            _stopwatch?.Stop();
            _stopwatch = null;
        }

        private void OpenCaptureDirectory()
        {
            try
            {
                Directory.CreateDirectory(SaveDirectory);
                Process.Start(new ProcessStartInfo
                {
                    FileName = SaveDirectory,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                UpdateStatsBadge($"无法打开目录: {ex.Message}");
            }
        }

        protected override Task OnPacketReceivedAsync(int inputPinIndex, BlueprintPacket packet, CancellationToken ct)
        {
            if (!_recordToggleParam.BoolValue) return Task.CompletedTask;

            long rowCountSnapshot = 0;
            string fileSnapshot = string.Empty;

            lock (_fileLock)
            {
                if (_writer == null) return Task.CompletedTask;

                if (!_headerWritten)
                {
                    if (packet.Channels.Length > 0)
                    {
                        var cols = new string[packet.Channels.Length + 2];
                        cols[0] = "Timestamp";
                        cols[1] = "ElapsedMs";
                        for (int i = 0; i < packet.Channels.Length; i++) cols[i + 2] = $"CH{i}";
                        _writer.WriteLine(string.Join(",", cols));
                    }
                    else
                    {
                        _writer.WriteLine("Timestamp,ElapsedMs,RawPayload");
                    }
                    _headerWritten = true;
                }

                long elapsed = _stopwatch?.ElapsedMilliseconds ?? 0;
                string ts = packet.Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);

                if (packet.Channels.Length > 0)
                {
                    _writer.Write(ts);
                    _writer.Write(',');
                    _writer.Write(elapsed.ToString(CultureInfo.InvariantCulture));
                    for (int i = 0; i < packet.Channels.Length; i++)
                    {
                        _writer.Write(',');
                        _writer.Write(packet.Channels[i].ToString("F4", CultureInfo.InvariantCulture));
                    }
                    _writer.WriteLine();
                }
                else
                {
                    string escaped = "\"" + packet.RawText.Replace("\"", "\"\"") + "\"";
                    _writer.WriteLine($"{ts},{elapsed},{escaped}");
                }

                _savedRows++;
                rowCountSnapshot = _savedRows;
                fileSnapshot = _currentFileName;

                if (_savedRows % 25 == 0)
                {
                    _writer.Flush();
                }
            }

            if (rowCountSnapshot == 1 || rowCountSnapshot % 20 == 0)
            {
                UpdateStatsBadge($"已写入 {rowCountSnapshot} 行 · {fileSnapshot}");
            }

            return Task.CompletedTask;
        }

        private void UpdateStatsBadge(string text)
        {
            string clipped = text.Length > 34 ? text[..34] + "…" : text;
            Application.Current?.Dispatcher.BeginInvoke(() => _statsBadgeParam.SetValueSilently(clipped));
        }

        public override void Dispose()
        {
            lock (_fileLock)
            {
                StopRecordingCore();
            }
            base.Dispose();
        }
    }
}
