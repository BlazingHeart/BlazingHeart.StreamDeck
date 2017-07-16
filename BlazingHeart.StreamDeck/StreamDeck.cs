using HidLibrary;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace BlazingHeart.StreamDeck
{
    public class StreamDeck : IDisposable
    {
        public delegate void InsertedEventHandler();
        public delegate void RemovedEventHandler();
        public delegate void KeyEventHandler(int key, bool state);

        public event InsertedEventHandler Inserted;
        public event RemovedEventHandler Removed;
        public event KeyEventHandler KeyChanged;

        const int VendorId = 0x0FD9;
        const int ProductId = 0x0060;

        public const int ImageWidth = 72;
        public const int ImageHeight = 72;
        public const int ImagePixels = ImageWidth * ImageHeight;

        public const int ScreenImageWidth = ImageWidth * 5;
        public const int ScreenImageHeight = ImageHeight * 3;

        public const int Gap = 24; // less than 36
        public const int ScreenGapsImageWidth = ImageWidth * 5 + Gap * 4;
        public const int ScreenGapsImageHeight = ImageHeight * 3 + Gap * 2;

        const int PAGE_PACKET_SIZE = 8191;
        const int NUM_FIRST_PAGE_PIXELS = 2583;
        const int NUM_SECOND_PAGE_PIXELS = 2601;

        const int FirstPageImageBytes = 7749;
        const int SecondPageImageBytes = 7803;
        const int ImageBytes = 15552;

        HidDevice _hidDevice;
        bool _attached;
        int _isReading;
        bool[] _keyState = new bool[16];

        public string Manufacturer { get; private set; }
        public string ProductName { get; private set; }
        public string FirmwareVersion { get; private set; }


        private StreamDeck(HidDevice hidDevice)
        {
            _hidDevice = hidDevice;
            _hidDevice.Inserted += StreamDeckInserted;
            _hidDevice.Removed += StreamDeckRemoved;
            _hidDevice.MonitorDeviceEvents = true;
        }

        public static StreamDeck Get()
        {
            var hidDevice = HidDevices.Enumerate(VendorId, ProductId).FirstOrDefault();
            if (hidDevice != null)
            {
                var device = new StreamDeck(hidDevice);
                return device;
            }
            return null;
        }

        public void Open()
        {
            _hidDevice.OpenDevice(DeviceMode.Overlapped, DeviceMode.Overlapped, ShareMode.ShareRead | ShareMode.ShareWrite);
            _hidDevice.ReadManufacturer(out byte[] manufacturerBytes);
            Manufacturer = Encoding.Unicode.GetString(manufacturerBytes).TrimEnd('\0');
            _hidDevice.ReadProduct(out byte[] productBytes);
            ProductName = Encoding.Unicode.GetString(productBytes).TrimEnd('\0');

            GetInfo();
            BeginReadReport();
        }

        private void GetInfo()
        {
            _hidDevice.ReadFeatureData(out byte[] data, 0x4);
            FirmwareVersion = Encoding.ASCII.GetString(data, 5, 12).TrimEnd('\0');
        }

        public void Close()
        {
            _hidDevice.CloseDevice();
        }

        public async Task SetScreenImage(Bitmap bitmap)
        {
            if (bitmap.Width != ScreenImageWidth || bitmap.Height != ScreenImageHeight)
            {
                throw new InvalidOperationException("Image Width and Height must be 72");
            }

            if (bitmap.PixelFormat != PixelFormat.Format24bppRgb)
            {
                throw new InvalidOperationException("Image format must be PixelFormat.Format24bppRgb");
            }

            var keyBitmap = new Bitmap(ImageWidth, ImageHeight, PixelFormat.Format24bppRgb);
            var destRect = new Rectangle(0, 0, ImageWidth, ImageHeight);
            using (Graphics g = Graphics.FromImage(keyBitmap))
            {
                for (int row = 0; row < 3; ++row)
                {
                    for (int col = 0; col < 5; ++col)
                    {
                        int x = col * ImageWidth;
                        int y = row * ImageHeight;
                        g.DrawImage(bitmap, destRect, new Rectangle(x, y, ImageWidth, ImageHeight), GraphicsUnit.Pixel);

                        await SetKeyImage((row * 5) + (4 - col), keyBitmap);
                    }
                }
            }
        }

        public async Task SetKeyImage(int keyId, Bitmap bitmap)
        {
            if (keyId < 0 || keyId > 15)
            {
                throw new InvalidOperationException("keyId must be 0-15");
            }

            if (bitmap.Width != ImageWidth || bitmap.Height != ImageHeight)
            {
                throw new InvalidOperationException("Image Width and Height must be 72");
            }

            if (bitmap.PixelFormat != PixelFormat.Format24bppRgb)
            {
                throw new InvalidOperationException("Image format must be PixelFormat.Format24bppRgb");
            }

            var data = bitmap.LockBits(new Rectangle(Point.Empty, bitmap.Size), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);

            byte[] buffer = new byte[ImageBytes];

            Marshal.Copy(data.Scan0, buffer, 0, ImageBytes);

            bitmap.UnlockBits(data);

            await SetKeyImage(keyId, buffer);
        }

        public async Task SetKeyImage(int keyId, byte[] imageBuffer)
        {
            if (imageBuffer.Length != ImageBytes)
            {
                throw new InvalidOperationException($"imageBuffer.Length must be {ImageBytes}");
            }

            byte[] newBuffer = new byte[ImageBytes];
            for (int row = 0; row < ImageWidth; ++row)
            {
                int rowOffset = row * ImageWidth * 3;

                for (int col = 0; col < ImageWidth; ++col)
                {
                    int colOffset = (ImageWidth - col - 1) * 3;
                    newBuffer[rowOffset + (col * 3) + 0] = imageBuffer[rowOffset + colOffset + 0];
                    newBuffer[rowOffset + (col * 3) + 1] = imageBuffer[rowOffset + colOffset + 1];
                    newBuffer[rowOffset + (col * 3) + 2] = imageBuffer[rowOffset + colOffset + 2];
                }
            }

            await SendImageData(keyId, newBuffer);
        }

        public void SetBrightness(int brightness)
        {
            if (brightness < 0 || brightness > 100)
            {
                throw new InvalidOperationException("brightness must be 0-100");
            }

            var buffer = new byte[]{
                0x05, 0x55, 0xaa, 0xd1, 0x01, (byte)brightness
            };

            _hidDevice.WriteFeatureData(buffer);
        }

        public void ShowLogo()
        {
            var buffer = new byte[]{
                0x0B, 0x63
            };

            _hidDevice.WriteFeatureData(buffer);
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="keyId"></param>
        /// <param name="buffer">72x72 BGR, reversed rows</param>
        /// <returns></returns>
        private async Task SendImageData(int keyId, byte[] buffer)
        {
            if (buffer.Length != ImageBytes)
            {
                throw new InvalidOperationException($"imageBuffer.Length must be {ImageBytes}");
            }

            byte[] packet = new byte[PAGE_PACKET_SIZE];

            byte[] page1Header = new byte[] {
               0x02, 0x01, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
               0x42, 0x4d, 0xf6, 0x3c, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x36, 0x00, 0x00, 0x00, 0x28, 0x00,
               0x00, 0x00, 0x48, 0x00, 0x00, 0x00, 0x48, 0x00, 0x00, 0x00, 0x01, 0x00, 0x18, 0x00, 0x00, 0x00,
               0x00, 0x00, 0xc0, 0x3c, 0x00, 0x00, 0xc4, 0x0e, 0x00, 0x00, 0xc4, 0x0e, 0x00, 0x00, 0x00, 0x00,
               0x00, 0x00, 0x00, 0x00, 0x00, 0x00
            };

            for (int i = 0; i < page1Header.Length; ++i)
            {
                packet[i] = page1Header[i];
            }

            packet[5] = (byte)(keyId + 1);

            const int imageOffset = 70;

            for (int i = 0; i < FirstPageImageBytes; ++i)
            {
                packet[imageOffset + i] = buffer[i];
            }

            await _hidDevice.WriteAsync(packet);

            for (int i = 0; i < packet.Length; ++i)
            {
                packet[i] = 0;
            }

            byte[] page2Header = new byte[] {
               0x02, 0x01, 0x02, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            };

            for (int i = 0; i < page2Header.Length; ++i)
            {
                packet[i] = page2Header[i];
            }

            packet[5] = (byte)(keyId + 1);

            const int imageOffset2 = 16;

            for (int i = 0; i < SecondPageImageBytes; ++i)
            {
                packet[imageOffset2 + i] = buffer[FirstPageImageBytes + i];
            }

            await _hidDevice.WriteAsync(packet);
        }

        private void BeginReadReport()
        {
            if (Interlocked.CompareExchange(ref _isReading, 1, 0) == 1) return;
            _hidDevice.ReadReport(ReadReport);
        }

        private void ReadReport(HidReport hidReport)
        {
            var report = new Report(hidReport);

            if (report.ReadStatus == HidDeviceData.ReadStatus.Success)
            {
                if (report.Data.Length == 16)
                {
                    StringBuilder sb = new StringBuilder(16);
                    for (int i = 0; i < 16; ++i)
                    {
                        bool newState = report.Data[i] != 0;

                        if (_keyState[i] != newState)
                        {
                            KeyChanged?.Invoke(i, newState);
                        }

                        _keyState[i] = newState;
                        sb.Append(_keyState[i] ? '1' : '0');
                    }

                    Debug.WriteLine($"Keys: {sb.ToString()}");
                }
            }

            if (_attached && report.ReadStatus != HidDeviceData.ReadStatus.NotConnected)
            {
                _hidDevice.ReadReport(ReadReport);
            }
            else
            {
                _isReading = 0;
            }
        }

        private void StreamDeckInserted()
        {
            _attached = true;
            BeginReadReport();
            Inserted?.Invoke();
        }

        private void StreamDeckRemoved()
        {
            _attached = false;
            Removed?.Invoke();
        }

        public Bitmap CreateKeyBitmap()
        {
            return new Bitmap(ImageWidth, ImageHeight, PixelFormat.Format24bppRgb);
        }

        public void Dispose()
        {
            _hidDevice.CloseDevice();
            GC.SuppressFinalize(this);
        }

        ~StreamDeck()
        {
            Dispose();
        }
    }
}
