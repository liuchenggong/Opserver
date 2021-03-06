using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Web;
using System.Web.Mvc;
using System.Xml;
using System.Xml.Xsl;
using EnumsNET;
using StackExchange.Opserver;
using StackExchange.Opserver.Data;
using StackExchange.Opserver.Data.Dashboard;
using StackExchange.Opserver.Data.SQL;
using StackExchange.Opserver.Helpers;

namespace StackExchange.Opserver.Models
{
    public static class ExtensionMethods
    {
        public static string ToSpeed(this float bps, string unit = "b") =>
            bps < 1 ? "0 b/s" : $"{bps.ToSize(unit)}/s";
    }

    public static class VolumeExtensionMethods
    {
        public static string PercentFreeSpace(this Volume vol) => (100 - vol.PercentUsed)?.ToString("n0") + "% Free";

        public static string PrettyRead(this Volume i) => i.ReadBps?.ToSpeed();

        public static string PrettyWrite(this Volume i) => i.WriteBps?.ToSpeed();
    }

    public static class InterfaceExtensionMethods
    {
        public static string PrettyIn(this Interface i) => i.InBps?.ToSpeed();

        public static string PrettyOut(this Interface i) => i.OutBps?.ToSpeed();
    }

    public static class NodeExtensionMethods
    {
        public static IHtmlString LastUpdatedSpan(this Node info)
        {
            var addClass = "";
            if (info.LastSync < DateTime.UtcNow.AddMinutes(-30))
            {
                addClass = MonitorStatus.Critical.TextClass();
            }
            else if (info.LastSync < DateTime.UtcNow.AddMinutes(-15))
            {
                addClass = MonitorStatus.Warning.TextClass();
            }
            return info.LastSync?.ToRelativeTimeSpan(addClass) ?? "Unknown".AsHtml();
        }

        public static string PrettyTotalMemory(this Node info) => info.TotalMemory?.ToSize() ?? "";

        public static string PrettyMemoryUsed(this Node info) => info.MemoryUsed?.ToSize() ?? "";

        public static MonitorStatus MemoryMonitorStatus(this Node info)
        {
            if (!info.PercentMemoryUsed.HasValue) return MonitorStatus.Unknown;
            if (info.MemoryCriticalPercent > 0 && info.PercentMemoryUsed > (float) info.MemoryCriticalPercent) return MonitorStatus.Critical;
            if (info.MemoryWarningPercent > 0 && info.PercentMemoryUsed > (float) info.MemoryWarningPercent) return MonitorStatus.Warning;
            return MonitorStatus.Good;
        }

        public static IHtmlString MemoryStatusSpan(this Node info, bool includePercent = true)
        {
            return info.MemoryUsed < 0
                ? MvcHtmlString.Empty
                : includePercent
                    ? $@"<span class=""{info.MemoryMonitorStatus().TextClass()}"">{info.PrettyMemoryUsed()} / {info.PrettyTotalMemory()} ({info.PercentMemoryUsed?.ToString("n2")}%)</span>".AsHtml()
                    : $@"<span class=""{info.MemoryMonitorStatus().TextClass()}"">{info.PrettyMemoryUsed()} / {info.PrettyTotalMemory()}</span>".AsHtml();
        }

        public static IHtmlString MemoryPercentStatusSpan(this Node info)
        {
            return info.MemoryUsed < 0
                ? MvcHtmlString.Empty
                : $@"<span title=""{info.PrettyMemoryUsed()} / {info.PrettyTotalMemory()}"" class=""{info.MemoryMonitorStatus().TextClass()}"">{info.PercentMemoryUsed?.ToString("n0")}%</span>".AsHtml();
        }

        public static MonitorStatus CPUMonitorStatus(this Node info)
        {
            if (!info.CPULoad.HasValue) return MonitorStatus.Unknown;
            if (info.CPUCriticalPercent > 0 && info.CPULoad > info.CPUCriticalPercent) return MonitorStatus.Critical;
            if (info.CPUWarningPercent > 0 && info.CPULoad > info.CPUWarningPercent) return MonitorStatus.Warning;
            return MonitorStatus.Good;
        }

        public static IHtmlString CPUStatusSpan(this Node info)
        {
            if (info == null || info.CPULoad < 0) return MvcHtmlString.Empty;
            return $@"<span class=""{info.CPUMonitorStatus().AsString(EnumFormat.Description)}"">{info.CPULoad?.ToString("n0")} %</span>".AsHtml();
        }

        public static string PrettyTotalNetwork(this Node info) =>
            info.TotalPrimaryNetworkbps < 0
                ? null
                : info.TotalPrimaryNetworkbps.ToSpeed();

        public static string PrettyTotalVolumePerformance(this Node info) =>
            info.TotalVolumePerformancebps < 0
                ? null
                : info.TotalVolumePerformancebps.ToSpeed();
    }

    public static class SQLExtenstions
    {
        private static readonly XslCompiledTransform _queryPlanTransform;

        static SQLExtenstions()
        {
            _queryPlanTransform = new XslCompiledTransform();
            _queryPlanTransform.Load(Current.Context.Server.MapPath("~/Content/transforms/qp.xslt"));
        }

        //XNamespace ns = "http://schemas.microsoft.com/sqlserver/2004/07/showplan";
        public static IHtmlString QueryPlanHtml(this SQLInstance.TopOperation op)
        {
            if (op.QueryPlan == null) return null;

            using (var sr = new StringReader(op.QueryPlan))
            using (var xmlr = XmlReader.Create(sr))
            {
                using (var sw = new StringWriter())
                using (var xmlw = XmlWriter.Create(sw, _queryPlanTransform.OutputSettings))
                {
                    _queryPlanTransform.Transform(xmlr, xmlw);
                    return sw.ToString().AsHtml();
                }
            }
        }
    }
}