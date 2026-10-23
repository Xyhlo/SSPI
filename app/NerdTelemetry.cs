using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace Orbis
{
    /// <summary>Live download telemetry for nerd stats UI. No tokens/URLs with secrets.</summary>
    internal sealed class NerdTelemetry
    {
        public const int Cap = 72;
        public readonly double[] Mbps = new double[Cap];
        public int Count;
        public int Write;
        public string TitleId = "";
        public string FileName = "";
        public string CdnHost = "";
        public string RegionHint = "";
        public string TransferMode = "";
        public long Done;
        public long Total;
        public bool Finalizing;
        public double MbpsInstant;
        public double MbpsAvg;
        public double Stability01 = 1;
        public int StallEvents;
        public int DropEvents;
        public string StabilityLabel = "—";

        public void Reset()
        {
            Count = 0; Write = 0;
            TitleId = FileName = CdnHost = RegionHint = TransferMode = "";
            Done = Total = 0;
            Finalizing = false;
            MbpsInstant = MbpsAvg = 0;
            Stability01 = 1;
            StallEvents = DropEvents = 0;
            StabilityLabel = "idle";
        }

        public void PushMbps(double mbps)
        {
            if (mbps < 0) mbps = 0;
            Mbps[Write] = mbps;
            Write = (Write + 1) % Cap;
            if (Count < Cap) Count++;
            MbpsInstant = mbps;
            double sum = 0, sum2 = 0;
            int n = Count;
            for (int i = 0; i < n; i++)
            {
                double v = Mbps[i];
                sum += v;
                sum2 += v * v;
            }
            MbpsAvg = n > 0 ? sum / n : 0;
            if (n < 4 || MbpsAvg < 0.05)
            {
                Stability01 = 1;
                StabilityLabel = n < 4 ? "warming" : "idle";
                return;
            }
            double var = Math.Max(0, (sum2 / n) - (MbpsAvg * MbpsAvg));
            double std = Math.Sqrt(var);
            double cov = std / Math.Max(0.05, MbpsAvg);
            Stability01 = Math.Max(0, Math.Min(1, 1.0 - cov));
            if (Stability01 >= 0.82) StabilityLabel = "stable";
            else if (Stability01 >= 0.55) StabilityLabel = "jitter";
            else StabilityLabel = "unstable";
        }

        public double SampleAt(int age)
        {
            if (Count <= 0) return 0;
            int idx = (Write - 1 - age + Cap * 4) % Cap;
            if (age >= Count) return 0;
            return Mbps[idx];
        }
    }
}
