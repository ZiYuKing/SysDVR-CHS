﻿using ImGuiNET;
using SysDVR.Client.Core;
using SysDVR.Client.Platform;
using SysDVR.Client.Sources;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace SysDVR.Client.GUI
{
    internal class NetworkScanView : View
    {
        readonly NetworkScan scanner = new();
        readonly List<DeviceInfo> devices = new List<DeviceInfo>();
        readonly byte[] IpAddressTextBuf = new byte[256];

        Gui.Popup ipEnterPopup = new("Enter console IP address");
        Gui.CenterGroup manualIpCenter = new();
        Gui.CenterGroup popupBtnCenter = new();
        string? lastError;

        public NetworkScanView()
        {
            scanner.OnDeviceFound += OnDeviceFound;
            scanner.OnFailure += OnFailure;
        }

        private void OnDeviceFound(DeviceInfo info)
        {
            lock (this)
            {
                devices.Add(info);
            }
        }

        private void OnFailure(string obj)
        {
            lock (this)
            {
                lastError = obj;
            }
        }

        public override void EnterForeground()
        {
            scanner.StartScanning();
            base.EnterForeground();
        }

        public override void LeaveForeground()
        {
            scanner.StopScannning();
            base.LeaveForeground();
        }

        public override void BackPressed()
        {
            if (ipEnterPopup.HandleBackButton())
                return;

            base.BackPressed();
        }

        public override void ResolutionChanged()
        {
            ipEnterPopup.OnResize();
        }

        void ButtonEnterIp()
        {
            ipEnterPopup.RequestOpen();
        }

        void ConenctToDevice(DeviceInfo info)
        {

        }

        public override void Draw()
        {
            var portrait = Program.Instance.IsPortrait;

            Gui.BeginWindow("Network scanner");

            Gui.CenterText("Searching for network devices...");
            ImGui.NewLine();

            var win = ImGui.GetWindowSize();
            var sz = win;

            sz.Y *= portrait ? .5f : .4f;
            sz.X *= portrait ? .92f : .82f;
            
            lock (this)
            {
                ImGui.SetCursorPosX(win.X / 2 - sz.X / 2);
                ImGui.BeginChildFrame(ImGui.GetID("##DevList"), sz, ImGuiWindowFlags.NavFlattened);
                var btn = new Vector2(sz.X, 0);
                foreach (var dev in devices)
                {
                    if (ImGui.Button(dev.ToString(), btn))
                        ConenctToDevice(dev);
                }
                ImGui.EndChildFrame();
                ImGui.NewLine();
            }

            if (portrait)
            {
                Gui.CenterText("Can't find your device ?");
                if (Gui.CenterButton("Use IP address"))
                    ButtonEnterIp();
            }
            else
            {
                manualIpCenter.StartHere();
                ImGui.TextWrapped("Can't find your device ?   ");
                ImGui.SameLine();

                if (ImGui.Button("Use IP address"))
                    ButtonEnterIp();

                manualIpCenter.EndHere();
            }

            if (lastError is not null)
            {
                ImGui.Text(lastError);
            }

            var styley = ImGui.GetStyle().WindowPadding.Y;
            sz.Y = ImGui.CalcTextSize("AAA").Y + styley * 2;
            ImGui.SetCursorPosY(win.Y - sz.Y - styley * 2);
            if (Gui.CenterButton("Go back", sz))
            {
                Program.Instance.PopView();
            }

            DrawIpEnterPopup();

            Gui.EndWindow();
        }

        void DrawIpEnterPopup() 
        {
            if (ipEnterPopup.Begin())
            {
                ImGui.InputText("##ip", IpAddressTextBuf, (uint)IpAddressTextBuf.Length);
                ImGui.Spacing();
                popupBtnCenter.StartHere();
                ImGui.Button("   Connect   ");
                ImGui.SameLine();
                if (ImGui.Button("    Cancel    "))
                    ipEnterPopup.RequestClose();
                popupBtnCenter.EndHere();
                ImGui.NewLine();

                ImGui.EndPopup();
            }
        }
    }
}
