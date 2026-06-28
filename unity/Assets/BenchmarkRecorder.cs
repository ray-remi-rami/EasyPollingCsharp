using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace FastInputBench
{
    // One row = one 1-second measurement window.
    public struct BenchSample
    {
        public double timeSec;
        public int fps, targetFps, kbPollSetHz;
        public double kbPollActualHz;
        public string nativeMode;
        // throughput (counts) + min event-time gap (ms), per system per device
        public int natKbN, natMouseN, isysKbN, isysMouseN, legKbN, legMouseN;
        public double natKbGapMs, natMouseGapMs, isysKbGapMs, isysMouseGapMs, legKbGapMs, legMouseGapMs;
        // reaction latency (event -> observable by game code), ms avg/max
        public double natReactAvgMs, natReactMaxMs, isysReactAvgMs, isysReactMaxMs, legReactAvgMs, legReactMaxMs;
        // resource
        public double nativeCpuPct, procCpuPct, memMB;
        public double perFrameNatUs, perFrameIsysUs, perFrameLegUs;
    }

    // Reusable collector: accumulate per-second samples, export to CSV or readable text.
    public class BenchmarkRecorder
    {
        private readonly List<BenchSample> rows = new List<BenchSample>();
        public int Count => rows.Count;
        public void Add(BenchSample s) => rows.Add(s);
        public void Clear() => rows.Clear();

        private static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

        public string ToCsv()
        {
            var sb = new StringBuilder();
            sb.Append("time_s,fps,target_fps,kb_set_hz,kb_actual_hz,native_mode,")
              .Append("nat_kb_n,nat_kb_gap_ms,nat_mouse_n,nat_mouse_gap_ms,")
              .Append("isys_kb_n,isys_kb_gap_ms,isys_mouse_n,isys_mouse_gap_ms,")
              .Append("leg_kb_n,leg_kb_gap_ms,leg_mouse_n,leg_mouse_gap_ms,")
              .Append("nat_react_avg_ms,nat_react_max_ms,isys_react_avg_ms,isys_react_max_ms,leg_react_avg_ms,leg_react_max_ms,")
              .Append("native_cpu_pct,proc_cpu_pct,mem_mb,frame_nat_us,frame_isys_us,frame_leg_us\n");
            foreach (var r in rows)
            {
                sb.Append(F(r.timeSec)).Append(',').Append(r.fps).Append(',').Append(r.targetFps).Append(',')
                  .Append(r.kbPollSetHz).Append(',').Append(F(r.kbPollActualHz)).Append(',').Append(r.nativeMode).Append(',')
                  .Append(r.natKbN).Append(',').Append(F(r.natKbGapMs)).Append(',').Append(r.natMouseN).Append(',').Append(F(r.natMouseGapMs)).Append(',')
                  .Append(r.isysKbN).Append(',').Append(F(r.isysKbGapMs)).Append(',').Append(r.isysMouseN).Append(',').Append(F(r.isysMouseGapMs)).Append(',')
                  .Append(r.legKbN).Append(',').Append(F(r.legKbGapMs)).Append(',').Append(r.legMouseN).Append(',').Append(F(r.legMouseGapMs)).Append(',')
                  .Append(F(r.natReactAvgMs)).Append(',').Append(F(r.natReactMaxMs)).Append(',')
                  .Append(F(r.isysReactAvgMs)).Append(',').Append(F(r.isysReactMaxMs)).Append(',')
                  .Append(F(r.legReactAvgMs)).Append(',').Append(F(r.legReactMaxMs)).Append(',')
                  .Append(F(r.nativeCpuPct)).Append(',').Append(F(r.procCpuPct)).Append(',').Append(F(r.memMB)).Append(',')
                  .Append(F(r.perFrameNatUs)).Append(',').Append(F(r.perFrameIsysUs)).Append(',').Append(F(r.perFrameLegUs)).Append('\n');
            }
            return sb.ToString();
        }

        public string ToText()
        {
            var sb = new StringBuilder();
            sb.AppendLine("Input Capability Benchmark - recorded samples (1 row = 1 second window)");
            sb.AppendLine(new string('=', 78));
            foreach (var r in rows)
            {
                sb.AppendLine($"[t={F(r.timeSec)}s] fps={r.fps}/{(r.targetFps < 0 ? "uncapped" : r.targetFps.ToString())}  kbPoll set={r.kbPollSetHz}Hz actual={F(r.kbPollActualHz)}Hz  nativeMode={r.nativeMode}");
                sb.AppendLine("  Throughput (N / min event-gap ms)     KEYBOARD              MOUSE");
                sb.AppendLine($"    Native      :  {r.natKbN,5} / {F(r.natKbGapMs),-7}  |  {r.natMouseN,6} / {F(r.natMouseGapMs)}");
                sb.AppendLine($"    InputSystem :  {r.isysKbN,5} / {F(r.isysKbGapMs),-7}  |  {r.isysMouseN,6} / {F(r.isysMouseGapMs)}");
                sb.AppendLine($"    Legacy      :  {r.legKbN,5} / {F(r.legKbGapMs),-7}  |  {r.legMouseN,6} / {F(r.legMouseGapMs)}");
                sb.AppendLine($"  Reaction latency ms (avg/max): Native {F(r.natReactAvgMs)}/{F(r.natReactMaxMs)}   InputSystem {F(r.isysReactAvgMs)}/{F(r.isysReactMaxMs)}   Legacy {F(r.legReactAvgMs)}/{F(r.legReactMaxMs)}");
                sb.AppendLine($"  Resource: nativeCPU {F(r.nativeCpuPct)}%  procCPU {F(r.procCpuPct)}%  mem {F(r.memMB)}MB  frame-us nat {F(r.perFrameNatUs)} isys {F(r.perFrameIsysUs)} leg {F(r.perFrameLegUs)}");
                sb.AppendLine();
            }
            return sb.ToString();
        }

        // Writes both files; returns the paths written.
        public string SaveCsv(string path) { File.WriteAllText(path, ToCsv()); return path; }
        public string SaveText(string path) { File.WriteAllText(path, ToText()); return path; }
    }
}
