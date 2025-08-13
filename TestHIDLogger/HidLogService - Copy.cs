using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using HidLibrary;

namespace TestHID
{
    public sealed class HidLogService : IDisposable
    {
        private readonly int _vid;
        private readonly int _pid;
        private HidDevice _device;

        private readonly ConcurrentQueue<byte[]> _rxQueue = new ConcurrentQueue<byte[]>();
        private readonly AutoResetEvent _rxEvent = new AutoResetEvent(false);
        private volatile bool _reading;

        public event Action QueueTick; // báo cho UI “có data mới”

        public long ReportCount => _reportCount;
        private long _reportCount;

        public HidLogService(int vid, int pid)
        {
            _vid = vid;
            _pid = pid;
        }

        public bool Open()
        {
            Close();

            //_device = HidDevices.Enumerate(_vid, _pid)?.FirstOrDefault();
            _device = HidDevices.Enumerate().FirstOrDefault(dev =>
            {
                byte[] productBytes;
                if (dev.ReadProduct(out productBytes))
                {
                    string productName = Encoding.Unicode.GetString(productBytes).TrimEnd('\0');
                    return productName.Contains("RadTag");
                }
                return false;
            });

            if (_device == null)
            {
                return false;
            }

            if (!_device.IsOpen)
            {
                _device.OpenDevice();
            }

            if (!_device.IsOpen)
            {
                return false;
            }

            byte[] command = new byte[64];

            command[0] = 0x00; // Report ID
            command[1] = 0x55;
            command[2] = 0x55;
            command[3] = 0x02;
            command[4] = 0x00;
            command[5] = 0x01;
            command[6] = 0x00;

            bool success = _device.Write(command);

            _reading = true;
            // Đăng ký vòng đọc liên tục
            _device.ReadReport(OnReportReceived);
            return true;
        }

        public void Close()
        {
            _reading = false;
            if (_device != null)
            {
                
                if (_device.IsOpen)
                {
                    _device.CloseDevice();
                }

                _device.Dispose();
                _device = null;
            }
        }

        //private void OnReportReceived(HidReport report)
        //{
        //    try
        //    {

        //        if (report.ReadStatus == HidDeviceData.ReadStatus.Success && report.Data != null)
        //        {
        //            var raw = report.Data; // 64 bytes (thường vậy với HID input report)
        //            // Sao chép để an toàn:
        //            var copy = new byte[raw.Length];
        //            Buffer.BlockCopy(raw, 0, copy, 0, raw.Length);
        //            _rxQueue.Enqueue(copy);
        //            Interlocked.Increment(ref _reportCount);
        //            _rxEvent.Set();
        //            QueueTick?.Invoke();
        //        }
        //    }
        //    finally
        //    {
        //        // ĐĂNG KÝ ĐỌC TIẾP – rất quan trọng để không rơi gói
        //        try
        //        {
        //            //if (_reading && _device != null && _device.IsOpen)
        //            {
        //                _device.ReadReport(OnReportReceived);
        //            }
        //        }
        //        catch
        //        {
        //            // thiết bị rút nóng hoặc lỗi IO
        //            _reading = false;
        //        }
        //    }
        //}

        private void OnReportReceived(HidReport report)
        {
            // 1) Re-arm sớm
            if (_reading && _device != null && _device.IsConnected)
            {
                try { _device.ReadReport(OnReportReceived); }
                catch { _reading = false; return; }
            }

            // 2) Chỉ enqueue
            if (report.ReadStatus == HidDeviceData.ReadStatus.Success && report.Data != null)
            {
                var copy = new byte[report.Data.Length];
                Buffer.BlockCopy(report.Data, 0, copy, 0, copy.Length);
                _rxQueue.Enqueue(copy);
                System.Threading.Interlocked.Increment(ref _reportCount);

                // Đừng set event mỗi gói; nhóm lại
                if ((System.Threading.Interlocked.Increment(ref _reportCount) & 0x0F) == 0)
                    _rxEvent.Set();
            }
        }

        public bool TryDequeue(out byte[] packet) => _rxQueue.TryDequeue(out packet);

        public void Dispose()
        {
            Close();
            _rxEvent.Dispose();
        }
    }
}
