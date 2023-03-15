﻿using LibUsbDotNet.LibUsb;
using LibUsbDotNet.Main;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace SysDVR.Client.Sources
{
	// Making UsbContext non static will remove requirement on libusb, it will be loaded only if actually using USB, this is useful on architectures where it's not supported
	class UsbContext : IDisposable
	{
		public enum LogLevel 
		{
			Error,
			Warning,
			Debug,
			None
		}

		readonly LibUsbDotNet.LibUsb.UsbContext LibUsbCtx = null;
		readonly bool ForceLibUsb;

		private LogLevel _debugLevel;
		public LogLevel DebugLevel {
			set {
				_debugLevel = value;
				LibUsbCtx.SetDebugLevel(value switch
				{
					LogLevel.Error => LibUsbDotNet.LogLevel.Error,
					LogLevel.Warning => LibUsbDotNet.LogLevel.Info,
					LogLevel.Debug => LibUsbDotNet.LogLevel.Debug,
					_ => LibUsbDotNet.LogLevel.None,
				});
			}
			get => _debugLevel;
		}

		public IUsbDevice device { get; private set; }
		
		public UsbContext(LogLevel logLevel = LogLevel.Error, bool forceLibUsb = false) 
		{
			ForceLibUsb = forceLibUsb;

			LibUsbCtx = new LibUsbDotNet.LibUsb.UsbContext();

			DebugLevel = logLevel;
		}

        static bool MatchSysdvrDevice(IUsbDevice device)
        {
            try
            {
				return device.VendorId == 0x18D1 && device.ProductId == 0x4EE0;
			}
			catch (Exception ex)
			{
                Console.WriteLine("Warning: failed to query device info " + ex);
                return false;
            }
        }

        public unsafe IReadOnlyList<(IUsbDevice, string)> FindSysdvrDevices() 
		{
			if (device != null)
				throw new Exception("device has already been set");

			// THis is hacky but libusb can't seem to get the device serial without opening it first
			// If the device is already opened by another instance of sysdvr it will print an error, suppress it by temporarily changing the log level 
			var old = DebugLevel;
			DebugLevel = LogLevel.None;

			var res = LibUsbCtx.List().Where(MatchSysdvrDevice).Select(x => {
				if (!x.TryOpen())
					return (null, null);

				var serial = x.Info.SerialNumber.ToLower().Trim();
                x.Close();

				if (!serial.StartsWith("sysdvr:"))
					return (null, null);

				return (x, serial[7..]);
			}).Where(x => x.Item2 != null).ToArray();
			
			DebugLevel = old;
			return res;
		}

		public void OpenUsbDevice(IUsbDevice device)
		{
			if (this.device != null)
				throw new Exception("device has already been set");

			this.device = device;
			this.device.Open();
		}

		public (UsbEndpointReader, UsbEndpointWriter) OpenEndpointPair()
		{
			var dev = device;

			if (!dev.ClaimInterface(0))
				throw new Exception($"Couldn't claim device interface");

			var (epIn, epOut) = (ReadEndpointID.Ep01, WriteEndpointID.Ep01);

			var reader = dev.OpenEndpointReader(epIn, PacketHeader.MaxTransferSize, EndpointType.Bulk);
			var writer = dev.OpenEndpointWriter(epOut, EndpointType.Interrupt);

			return (reader, writer);
		}

		public void CloseDevice()
		{
            device.Close();
            device = null;
        }

		public UsbStreamingSourceBase CreateStreamingSource(bool HasVideo, bool HasAudio)
		{
			UsbStreamingSourceBase source;
            
			// LibUsb backend on linux and windows doesn't seem to buffer reads from the pipe, this causes issues and requires manually receiving everything and copying data
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && !ForceLibUsb)
                source = new UsbStreamingSourceWinUsb(this);
			else
                source = new UsbStreamingSourceLibUsb(this);

			source.HasVideo = HasVideo;
			source.HasAudio = HasAudio;

			return source;
		}

		private bool disposedValue;
		protected virtual void Dispose(bool disposing)
		{
			if (!disposedValue)
			{
				if (disposing)
				{
					device?.Close();
					LibUsbCtx.Dispose();
				}

				disposedValue = true;
			}
		}

		~UsbContext()
		{
			Dispose(false);
		}

		public void Dispose()
		{
			Dispose(disposing: true);
			GC.SuppressFinalize(this);
		}
	}

	abstract class UsbStreamingSourceBase : IStreamingSource
	{
		protected static readonly byte[] MagicRequestVideo = { 0xBB, 0xBB, 0xBB, 0xBB };
		protected static readonly byte[] MagicRequestAudio = { 0xCC, 0xCC, 0xCC, 0xCC };
		protected static readonly byte[] MagicRequestBoth = { 0xAA, 0xAA, 0xAA, 0xAA };

		public bool Logging { get; set; }
		CancellationToken Token; 

		// TODO: Remove tracing code
		readonly private TimeTrace tracer = new();
		
		protected TimeTrace BeginTrace(string extra = "", [CallerMemberName] string funcName = null) => 
			tracer.Begin("usb", extra, funcName);

        protected UsbEndpointReader reader;
		protected UsbEndpointWriter writer;
		protected UsbContext context;

		public bool HasAudio;
		public bool HasVideo;

		byte[] RequestMagic => (HasVideo, HasAudio) switch 
		{
			(true, true) => MagicRequestBoth,
			(true, false) => MagicRequestVideo,
			(false, true) => MagicRequestAudio,
			_ => throw new Exception("Invalid state")
		};

        public StreamKind SourceKind => (HasVideo, HasAudio) switch
        {
            (true, true) => StreamKind.Both,
            (true, false) => StreamKind.Video,
            (false, true) => StreamKind.Audio,
            _ => throw new Exception("Invalid state")
        };

        public UsbStreamingSourceBase(UsbContext context)
		{
			this.context = context;
			(reader, writer) = context.OpenEndpointPair();
		}

		public void StopStreaming()
		{
			context.CloseDevice();
		}

		LibUsbDotNet.Error lastError = LibUsbDotNet.Error.Success;
		public void WaitForConnection() 
		{
			while (!Token.IsCancellationRequested) 
			{
				//using var trace = BeginTrace();
#if DEBUG
				Console.WriteLine($"Sending USB connection request");
#endif
                var err = writer.Write(RequestMagic, 1000, out int _);
				if (err != LibUsbDotNet.Error.Success)
				{
					if (err != lastError)
					{
						Console.WriteLine($"USB warning: Couldn't communicate with the console ({err}). Try entering a game, unplugging your console or restarting it.");
						lastError = err;
					}
					Thread.Sleep(3000);
					continue;
				}
				lastError = err;

				return;
			}
		}

		public virtual void Flush() 
		{
			// Wait some time so the switch side timeouts
			Thread.Sleep(3000);

            // Then attempt to connect again
            WaitForConnection();
		}

		// Provided by the underlying implementation
		public abstract bool ReadHeader(byte[] buffer);
		public abstract bool ReadPayload(byte[] buffer, int length);

		public void UseCancellationToken(CancellationToken tok)
		{
			Token = tok;
		}
	}

	class UsbStreamingSourceWinUsb : UsbStreamingSourceBase
	{
		public UsbStreamingSourceWinUsb(UsbContext context) : base(context) { }

		public override bool ReadHeader(byte[] buffer)
		{
			//using var trace = BeginTrace();

			var err = reader.Read(buffer, 0, PacketHeader.StructLength, 200, out int _);
			if (err != LibUsbDotNet.Error.Success)
			{
				if (Logging)
					Console.WriteLine($"Warning: winusb error {err} while reading header");
				return false;
			}

			return true;
		}

		public override bool ReadPayload(byte[] buffer, int length)
		{
			var err = reader.Read(buffer, 0, length, 200, out int sz);
			if (err != LibUsbDotNet.Error.Success)
			{
				if (Logging)
					Console.WriteLine($"Warning: winusb error {err} while reading payload. ({sz} read)");
				return false;
			}

			return sz == length;
		}
	}

	class UsbStreamingSourceLibUsb : UsbStreamingSourceBase
	{
		public UsbStreamingSourceLibUsb(UsbContext context) : base(context) { }

		public override void Flush()
		{
			base.Flush();
			ReadSize = 0;
		}
		
		//Incredibly dumb libusb workaround
		private byte[] ReadBuffer = new byte[PacketHeader.MaxTransferSize];
		private int ReadSize = 0;
		public override bool ReadHeader(byte[] buffer)
		{
			//using var trace = BeginTrace();

			var err = reader.Read(ReadBuffer, 0, PacketHeader.MaxTransferSize, 400, out ReadSize);
			if (err != LibUsbDotNet.Error.Success)
			{
				if (Logging)
					Console.WriteLine($"Warning: libusb error {err} while reading header");
				return false;
			}

			Buffer.BlockCopy(ReadBuffer, 0, buffer, 0, PacketHeader.StructLength);

			return true;
		}

		public override bool ReadPayload(byte[] buffer, int length)
		{
			if (length > ReadSize - PacketHeader.StructLength)
				return false;

			Buffer.BlockCopy(ReadBuffer, PacketHeader.StructLength, buffer, 0, length);

			return true;
		}
	}
}
