using HidLibrary;
using Microsoft.Win32.SafeHandles;
using System;

using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace TestHID
{
    using HidLibrary;
    using Microsoft.Win32.SafeHandles;
    using System;
    using System.Collections.Concurrent;
    using System.IO;
    using System.Linq;
    using System.Runtime.InteropServices;
    using System.Text;
    using System.Threading;

    public sealed class HidLogService : IDisposable
    {
        private readonly int _vid;
        private readonly int _pid;
        private HidDevice _device;

        private readonly ConcurrentQueue<byte[]> _rxQueue = new ConcurrentQueue<byte[]>();
        private readonly AutoResetEvent _rxEvent = new AutoResetEvent(false);
        private volatile bool _reading;

        public long ReportCount => _reportCount;
        private long _reportCount;
        private int _wakeCounter;

        // số ReadReport đang pending để luôn giữ pipeline đầy
        private int _inflightReads;
        private const int TargetInflightReads = 32;  // có thể tăng 6–8 nếu vẫn rơi

        // gọi _rxEvent.Set() mỗi batch gói để giảm context switch
        private const int WakeBatch = 16;
        // tăng HID kernel input buffers
        private const uint KernelInputBuffers = 256;

        public HidLogService(int vid, int pid)
        {
            _vid = vid;
            _pid = pid;
        }

        public bool Open()
        {
            Close();

            // Chọn theo ProductName chứa "RadTag" trước
            _device = HidDevices.Enumerate().FirstOrDefault(dev =>
            {
                byte[] productBytes;
                if (dev.ReadProduct(out productBytes))
                {
                    string productName = Encoding.Unicode.GetString(productBytes).TrimEnd('\0');
                    return productName.IndexOf("RadTag", StringComparison.OrdinalIgnoreCase) >= 0;
                }
                return false;
            });

            // Fallback theo VID/PID nếu cần
            if (_device == null && _vid != 0 && _pid != 0)
            {
                _device = HidDevices.Enumerate(_vid, _pid).FirstOrDefault();
            }
            if (_device == null) return false;

            if (!_device.IsOpen) _device.OpenDevice();
            if (!_device.IsOpen) return false;

            _device.MonitorDeviceEvents = false;
            

            // Tăng kernel HID input buffers (cần handle ReadWrite)
            try { BoostHidBuffers(_device.DevicePath, KernelInputBuffers); } catch { /*ignore*/ }

            // Gửi lệnh khởi tạo (nếu giao thức yêu cầu)
            try
            {
                var command = new byte[64];
                command[0] = 0x00; // ReportID
                command[1] = 0x55; command[2] = 0x55;
                command[3] = 0x02; command[4] = 0x00;
                command[5] = 0x01; command[6] = 0x00;
                _device.Write(command);
            }
            catch { /* có thể bỏ qua nếu thiết bị không cần */ }

            _reading = true;
            _reportCount = 0;
            _wakeCounter = 0;

            // ĐẶT N READS BAN ĐẦU (multi-inflight)
            _inflightReads = 0;
            for (int i = 0; i < TargetInflightReads; i++)
            {
                TryQueueRead();
            }

            return true;
        }

        public void Close()
        {
            _reading = false;
            try { _rxEvent.Set(); } catch { }
            if (_device != null)
            {
                try { if (_device.IsOpen) _device.CloseDevice(); }
                catch { }
                finally { _device.Dispose(); _device = null; }
            }
        }

        private void TryQueueRead()
        {
            if (!_reading || _device == null) return;
            try
            {
                Interlocked.Increment(ref _inflightReads);
                _device.ReadReport(OnReportReceived); // overlapped async
            }
            catch
            {
                Interlocked.Decrement(ref _inflightReads);
                _reading = false;
            }
        }

        /// Callback đọc: re-arm ngay và giữ số inflight ổn định
        private void OnReportReceived(HidReport report)
        {
            // LUÔN đặt một read mới để duy trì pipeline
            // (không check IsConnected/IsOpen ở đây để tránh gap)
            TryQueueRead();

            // giảm inflight của read vừa hoàn tất (đã được bù ở trên bởi TryQueueRead)
            Interlocked.Decrement(ref _inflightReads);

            // Enqueue dữ liệu (không UI/không parse tại đây)
            if (!_reading || report == null) return;
            if (report.ReadStatus == HidDeviceData.ReadStatus.Success && report.Data != null)
            {
                var src = report.Data;
                var copy = new byte[src.Length];
                Buffer.BlockCopy(src, 0, copy, 0, copy.Length);
                _rxQueue.Enqueue(copy);

                Interlocked.Increment(ref _reportCount);

                int w = Interlocked.Increment(ref _wakeCounter);
                if ((w % WakeBatch) == 0)
                    _rxEvent.Set();
            }
        }

        public bool TryDequeue(out byte[] packet) => _rxQueue.TryDequeue(out packet);

        public WaitHandle RxWaitHandle => _rxEvent;

        public void Dispose()
        {
            Close();
            _rxEvent.Dispose();
        }

        // ====== Boost kernel HID input buffers ======
        [DllImport("hid.dll", SetLastError = true)]
        private static extern bool HidD_SetNumInputBuffers(SafeFileHandle h, uint n);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern SafeFileHandle CreateFile(
            string lpFileName,
            FileAccess dwDesiredAccess,
            FileShare dwShareMode,
            IntPtr lpSecurityAttributes,
            FileMode dwCreationDisposition,
            int dwFlagsAndAttributes,
            IntPtr hTemplateFile);

        private static void BoostHidBuffers(string devicePath, uint buffers)
        {
            // MỞ HANDLE READWRITE theo yêu cầu của HidD_SetNumInputBuffers
            var h = CreateFile(devicePath,
                               FileAccess.ReadWrite,              // <-- quan trọng
                               FileShare.ReadWrite,
                               IntPtr.Zero, FileMode.Open, 0, IntPtr.Zero);
            if (!h.IsInvalid)
            {
                try { HidD_SetNumInputBuffers(h, buffers); }
                finally { h.Dispose(); }
            }
        }
    }


}

