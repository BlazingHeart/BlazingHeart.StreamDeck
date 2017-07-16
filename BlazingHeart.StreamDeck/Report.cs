using HidLibrary;
using System;

namespace BlazingHeart.StreamDeck
{
    internal class Report
    {
        private readonly byte[] _data;
        private readonly HidDeviceData.ReadStatus _status;

        public HidDeviceData.ReadStatus ReadStatus { get { return _status; } }
        public byte[] Data { get { return _data; } }
        public bool Exists { get; private set; }
        public byte ReportId { get; private set; }

        public Report(HidReport hidReport)
        {
            _status = hidReport.ReadStatus;
            ReportId = hidReport.ReportId;
            Exists = hidReport.Exists;

            _data = hidReport.Data;
        }
    }
}