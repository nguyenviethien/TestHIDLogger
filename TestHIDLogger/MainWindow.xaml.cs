using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using System.Windows.Threading;
using TestHID;

namespace TestHIDLogger
{
    /// <summary>
    /// MainWindow.xaml에 대한 상호 작용 논리
    /// </summary>
    public partial class MainWindow : Window
    {
        private HidLogService _svc;
        private readonly StringBuilder _sb = new StringBuilder(256 * 128);
        private readonly DispatcherTimer _uiTimer;
        private long _shownCount;

        public MainWindow()
        {
            InitializeComponent();

            //try
            //{
            //    Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.High;
            //}
            //catch
            //{

            //}

            Thread.CurrentThread.Priority = ThreadPriority.AboveNormal;

            _uiTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(20) // batch UI 20fps
            };
            _uiTimer.Tick += UiTimer_Tick;
        }

        private void BtnOpen_Click(object sender, RoutedEventArgs e)
        {
            int vid, pid;
            if (!TryParseHex(VidBox.Text, out vid) || !TryParseHex(PidBox.Text, out pid))
            {
                MessageBox.Show("VID/PID không hợp lệ. Dùng dạng 0x1234 hoặc số thập phân.");
                return;
            }

            _svc?.Dispose();
            _svc = new HidLogService(vid, pid);
            //_svc.QueueTick += OnQueueTick;

            if (_svc.Open())
            {
                StatusText.Text = $"Opened VID=0x{vid:X4}, PID=0x{pid:X4}";
                BtnOpen.IsEnabled = false;
                BtnClose.IsEnabled = true;
                _uiTimer.Start();
            }
            else
            {
                StatusText.Text = "Open failed";
                _svc.Dispose();
                _svc = null;
            }
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            _uiTimer.Stop();
            _svc?.Dispose();
            _svc = null;
            BtnOpen.IsEnabled = true;
            BtnClose.IsEnabled = false;
            StatusText.Text = "Closed";
        }

        private void BtnClear_Click(object sender, RoutedEventArgs e)
        {
            _sb.Clear();
            LogBox.Text = "";
            _shownCount = 0;
            CountText.Text = "Packets: 0";
        }

        private void OnQueueTick()
        {
            // Không cập nhật UI trực tiếp ở callback. Để timer kéo theo batch.
            // (No-op ở đây, nhưng có thể dùng để wake nếu bạn dùng AutoResetEvent).
        }

        private void UiTimer_Tick(object sender, EventArgs e)
        {
            if (_svc == null)
                return;

            int drained = 0;
            byte[] pkt;
            while (drained < 512 && _svc.TryDequeue(out pkt))
            {
                drained++;
                AppendPacketLine(pkt);
            }

            if (drained > 0)
            {
                LogBox.Text = _sb.ToString();
                CountText.Text = $"Packets: {_svc.ReportCount}";
                if (AutoScrollChk.IsChecked == true)
                {
                    LogBox.CaretIndex = LogBox.Text.Length;
                    LogBox.ScrollToEnd();
                }
            }
        }

        private void AppendPacketLine(byte[] data)
        {
            // In theo dạng thời gian + hex
            var ts = DateTime.Now.ToString("HH:mm:ss.fff");
            _sb.Append('[').Append(ts).Append("] len=").Append(data?.Length ?? 0).Append(" : ");

            if (data != null)
            {
                for (int i = 0; i < data.Length; i++)
                {
                    _sb.Append(data[i].ToString("X2")).Append(' ');
                }
            }
            _sb.AppendLine();
            _shownCount++;
            // Optional: cắt bớt log tránh phình quá lớn (ví dụ giữ tối đa ~100k dòng)
            if (_shownCount % 20000 == 0 && _sb.Length > 2000000)
            {
                _sb.Remove(0, _sb.Length / 2);
                _sb.Insert(0, $"--- Trimmed log at {_shownCount} packets ---{Environment.NewLine}");
            }
        }

        private static bool TryParseHex(string s, out int value)
        {
            s = s?.Trim();
            if (string.IsNullOrEmpty(s))
            { value = 0; return false; }
            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                return int.TryParse(s.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
            }

            return int.TryParse(s, out value);
        }

        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);
            _uiTimer.Stop();
            _svc?.Dispose();
        }
    }
}
