using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using MvCameraControl;
using CalibOperatorPInvoke;

namespace CalibOperatorCLI_Example
{
    /// <summary>
    /// 海康威视相机服务封装
    /// 枚举设备、打开、采图、关闭
    /// </summary>
    public class CameraService : IDisposable
    {
        private IDevice? _device;
        private bool _isGrabbing = false;
        private bool _disposed = false;

        public bool IsConnected => _device?.IsConnected ?? false;
        public bool IsGrabbing => _isGrabbing;
        public string? LastError { get; private set; }

        /// <summary>
        /// 回调取图事件：传出 FrameOutput
        /// </summary>
        public event Action<IFrameOut>? FrameGrabbed;

        /// <summary>
        /// 初始化 SDK（整个进程只需调用一次）
        /// </summary>
        public static void InitializeSDK()
        {
            SDKSystem.Initialize();
        }

        /// <summary>
        /// 反初始化 SDK
        /// </summary>
        public static void FinalizeSDK()
        {
            SDKSystem.Finalize();
        }

        /// <summary>
        /// 枚举所有设备
        /// </summary>
        public static List<IDeviceInfo> EnumDevices()
        {
            var devLayerType = DeviceTLayerType.MvGigEDevice
                | DeviceTLayerType.MvUsbDevice
                | DeviceTLayerType.MvGenTLCameraLinkDevice
                | DeviceTLayerType.MvGenTLCXPDevice
                | DeviceTLayerType.MvGenTLXoFDevice;

            int ret = DeviceEnumerator.EnumDevices(devLayerType, out var devInfoList);
            if (ret != MvError.MV_OK || devInfoList == null)
                return new List<IDeviceInfo>();

            return devInfoList;
        }

        /// <summary>
        /// 通过 IP 创建设备（适用于 GigE 相机）
        /// </summary>
        public bool ConnectByIP(string deviceIp, string? netExportIp = null)
        {
            Disconnect();

            try
            {
                if (!string.IsNullOrEmpty(netExportIp))
                    _device = DeviceFactory.CreateDeviceByIp(deviceIp, netExportIp);
                else
                    _device = DeviceFactory.CreateDeviceByIp(deviceIp, "");

                if (_device == null) return false;

                int ret = _device.Open();
                if (ret != MvError.MV_OK)
                {
                    _device.Dispose();
                    _device = null;
                    return false;
                }

                // GigE 相机自动探测最佳包大小
                if (_device is IGigEDevice gigeDevice)
                {
                    int packetSize;
                    gigeDevice.GetOptimalPacketSize(out packetSize);
                    if (packetSize > 0)
                    {
                        _device.Parameters.SetIntValue("GevSCPSPacketSize", packetSize);
                    }
                }

                // 设置触发模式为 off（连续采集）
                _device.Parameters.SetEnumValue("TriggerMode", 0);

                // 设置缓存节点数
                _device.StreamGrabber.SetImageNodeNum(5);

                return true;
            }
            catch
            {
                _device?.Dispose();
                _device = null;
                return false;
            }
        }

        /// <summary>
        /// 通过设备索引连接
        /// </summary>
        public bool ConnectByIndex(int index)
        {
            var devices = EnumDevices();
            if (index < 0 || index >= devices.Count) return false;

            Disconnect();

            try
            {
                _device = DeviceFactory.CreateDevice(devices[index]);
                if (_device == null) return false;

                int ret = _device.Open();
                if (ret != MvError.MV_OK)
                {
                    _device.Dispose();
                    _device = null;
                    return false;
                }

                if (_device is IGigEDevice gigeDevice)
                {
                    int packetSize;
                    gigeDevice.GetOptimalPacketSize(out packetSize);
                    if (packetSize > 0)
                        _device.Parameters.SetIntValue("GevSCPSPacketSize", packetSize);
                }

                _device.Parameters.SetEnumValue("TriggerMode", 0);
                _device.StreamGrabber.SetImageNodeNum(5);

                return true;
            }
            catch
            {
                _device?.Dispose();
                _device = null;
                return false;
            }
        }

        /// <summary>
        /// 注册回调并开始采集
        /// </summary>
        public bool StartGrabbing()
        {
            if (_device == null || !_device.IsConnected) return false;

            _device.StreamGrabber.FrameGrabedEventEx += OnFrameGrabbed;
            int ret = _device.StreamGrabber.StartGrabbing();
            if (ret != MvError.MV_OK) return false;

            _isGrabbing = true;
            return true;
        }

        /// <summary>
        /// 停止采集
        /// </summary>
        public bool StopGrabbing()
        {
            if (_device == null) return false;

            _isGrabbing = false;
            _device.StreamGrabber.FrameGrabedEventEx -= OnFrameGrabbed;
            int ret = _device.StreamGrabber.StopGrabbing();
            return ret == MvError.MV_OK;
        }

        /// <summary>
        /// 单次取图（同步方式，用于标定拍照）
        /// </summary>
        public CalibImage? GrabOneFrame(int targetWidth, int targetHeight)
        {
            if (_device == null || !_device.IsConnected) return null;

            try
            {
                // 开始取流
                int ret = _device.StreamGrabber.StartGrabbing();
                if (ret != 0) // MV_OK == 0
                {
                    LastError = $"StartGrabbing failed: 0x{ret:X8}";
                    return null;
                }

                // 使用 GetImageBuffer 同步获取一帧，3秒超时
                IFrameOut frameOut;
                ret = _device.StreamGrabber.GetImageBuffer(3000, out frameOut);
                if (ret != 0 || frameOut == null)
                {
                    LastError = $"GetImageBuffer failed: 0x{ret:X8}";
                    _device.StreamGrabber.StopGrabbing();
                    return null;
                }

                try
                {
                    if (frameOut.Image == null || frameOut.Image.PixelDataPtr == IntPtr.Zero)
                    {
                        LastError = "FrameOut image data is null";
                        return null;
                    }

                    return FrameToCalibImage(frameOut);
                }
                finally
                {
                    _device.StreamGrabber.StopGrabbing();
                    _device.StreamGrabber.FreeImageBuffer(frameOut);
                }
            }
            catch (Exception ex)
            {
                LastError = $"GrabOneFrame exception: {ex.Message}";
                return null;
            }
        }

        /// <summary>
        /// 断开连接
        /// </summary>
        public void Disconnect()
        {
            if (_isGrabbing)
                StopGrabbing();

            if (_device != null)
            {
                try { _device.Close(); } catch { }
                _device.Dispose();
                _device = null;
            }
        }

        /// <summary>
        /// 将相机帧转换为 CalibImage
        /// 相机输出为 BGR 8bit，转换为 CalibImage 的 BGR 格式
        /// </summary>
        private CalibImage? FrameToCalibImage(IFrameOut frameOut)
        {
            try
            {
                var camImg = frameOut.Image;
                if (camImg.PixelDataPtr == IntPtr.Zero || camImg.Width <= 0 || camImg.Height <= 0)
                {
                    frameOut.Dispose();
                    return null;
                }

                int channels = 3;  // BGR
                int width = (int)camImg.Width;
                int height = (int)camImg.Height;

                CalibImage calibImg = new CalibImage(width, height, channels);

                // CalibImage 的 rowSize 是 4 字节对齐的
                int dstRowSize = width * channels;
                if (dstRowSize % 4 != 0) dstRowSize = ((dstRowSize / 4) + 1) * 4;

                // 相机图像的 pixel format 需要判断
                // PixelType_GVSP_RGB8_Packed = 0x02180015 (RGB)
                // PixelType_GVSP_BGR8_Packed = 0x0218000F (BGR)
                // PixelType_GVSP_Mono8 = 0x01080001
                uint pixelFormat = (uint)camImg.PixelType;
                bool isMono = (pixelFormat == 0x01080001);  // Mono8
                bool isRGB = (pixelFormat == 0x02180015);   // RGB
                bool isBGR = (pixelFormat == 0x0218000F);   // BGR

                // 获取 NativeImage data 指针
                var native = calibImg.GetNativeStruct();
                IntPtr dstData = native.data;
                IntPtr srcData = camImg.PixelDataPtr;

                if (isMono)
                {
                    // 灰度 -> 转为 3 通道 BGR
                    channels = 3;
                    calibImg = new CalibImage(width, height, 3);
                    native = calibImg.GetNativeStruct();
                    dstData = native.data;
                    int srcRowSize = (int)camImg.Width;  // mono: 1 byte per pixel

                    unsafe
                    {
                        byte* dst = (byte*)dstData;
                        byte* src = (byte*)srcData;
                        for (int y = 0; y < height; y++)
                        {
                            for (int x = 0; x < width; x++)
                            {
                                byte gray = src[y * srcRowSize + x];
                                int dstIdx = y * dstRowSize + x * 3;
                                dst[dstIdx + 0] = gray;  // B
                                dst[dstIdx + 1] = gray;  // G
                                dst[dstIdx + 2] = gray;  // R
                            }
                        }
                    }
                }
                else if (isRGB || isBGR)
                {
                    int srcRowSize = (int)camImg.Width * 3;
                    if (isRGB)
                    {
                        // RGB -> BGR（CalibImage 使用 BGR 格式）
                        unsafe
                        {
                            byte* dst = (byte*)dstData;
                            byte* src = (byte*)srcData;
                            for (int y = 0; y < height; y++)
                            {
                                for (int x = 0; x < width; x++)
                                {
                                    int srcIdx = y * srcRowSize + x * 3;
                                    int dstIdx = y * dstRowSize + x * 3;
                                    dst[dstIdx + 0] = src[srcIdx + 2];  // B = R
                                    dst[dstIdx + 1] = src[srcIdx + 1];  // G = G
                                    dst[dstIdx + 2] = src[srcIdx + 0];  // R = B
                                }
                            }
                        }
                    }
                    else
                    {
                        // BGR -> BGR（直接拷贝，注意 pitch 对齐）
                        for (int y = 0; y < height; y++)
                        {
                            IntPtr srcRow = IntPtr.Add(srcData, y * srcRowSize);
                            IntPtr dstRow = IntPtr.Add(dstData, y * dstRowSize);
                            CopyMemory(dstRow, srcRow, (uint)(width * 3));
                        }
                    }
                }

                frameOut.Dispose();
                return calibImg;
            }
            catch (Exception)
            {
                frameOut?.Dispose();
                return null;
            }
        }

        private void OnFrameGrabbed(object sender, FrameGrabbedEventArgs e)
        {
            try
            {
                FrameGrabbed?.Invoke(e.FrameOut);
            }
            catch { }
            finally
            {
                e.FrameOut.Dispose();
            }
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                Disconnect();
                _disposed = true;
            }
            GC.SuppressFinalize(this);
        }

        [DllImport("kernel32.dll", EntryPoint = "RtlMoveMemory")]
        private static extern void CopyMemory(IntPtr dest, IntPtr src, uint length);
    }
}
