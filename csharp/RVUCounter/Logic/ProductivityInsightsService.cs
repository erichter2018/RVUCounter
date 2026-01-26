using RVUCounter.Data;
using RVUCounter.Models;
using Serilog;

namespace RVUCounter.Logic;

/// <summary>
/// Service for generating productivity insights from historical data.
/// </summary>
public class ProductivityInsightsService
{
    private readonly RecordsDatabase _database;

    public ProductivityInsightsService(RecordsDatabase database)
    {
        _database = database;
    }

    /// <summary>
    /// Analyzes productivity patterns from historical data.
    /// </summary>
    public ProductivityInsights AnalyzeProductivity(DateTime startDate, DateTime endDate)
    {
        var insights = new ProductivityInsights();

        try
        {
            // Get all shifts and records in range
            var shifts = _database.GetAllShifts()
                .Where(s => s.ShiftStart >= startDate && s.ShiftStart <= endDate)
                .ToList();

            if (shifts.Count == 0) return insights;

            var allRecords = new List<(StudyRecord record, Shift shift)>();
            foreach (var shift in shifts)
            {
                var records = _database.GetRecordsForShift(shift.Id);
                allRecords.AddRange(records.Select(r => (r, shift)));
            }

            if (allRecords.Count == 0) return insights;

            // Analyze by hour of day
            AnalyzeByHour(insights, allRecords);

            // Analyze by day of week
            AnalyzeByDayOfWeek(insights, allRecords);

            // Analyze by study type (speed)
            AnalyzeByStudyType(insights, allRecords);

            // Analyze by modality
            AnalyzeByModality(insights, allRecords);

            // Generate recommendations
            GenerateRecommendations(insights);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error analyzing productivity");
        }

        return insights;
    }

    private void AnalyzeByHour(ProductivityInsights insights, List<(StudyRecord record, Shift shift)> data)
    {
        // Group by hour and calculate RVU/hour for each
        var hourlyData = new Dictionary<int, (double totalRvu, int count)>();

        foreach (var (record, _) in data)
        {
            var hour = record.Timestamp.Hour;
            if (hourlyData.ContainsKey(hour))
            {
                var existing = hourlyData[hour];
                hourlyData[hour] = (existing.totalRvu + record.Rvu, existing.count + 1);
            }
            else
            {
                hourlyData[hour] = (record.Rvu, 1);
            }
        }

        // Find best hour (highest RVU per study - indicates efficiency)
        var bestHour = hourlyData
            .OrderByDescending(kv => kv.Value.count > 0 ? kv.Value.totalRvu / kv.Value.count : 0)
            .FirstOrDefault();

        if (bestHour.Key != 0 || hourlyData.ContainsKey(0))
        {
            insights.BestHourOfDay = bestHour.Key;
            insights.BestHourRvuPerHour = bestHour.Value.count > 0
                ? bestHour.Value.totalRvu / bestHour.Value.count
                : 0;
        }

        // Store hourly breakdown for charts
        foreach (var kv in hourlyData)
        {
            insights.RvuByHour[kv.Key] = kv.Value.totalRvu;
        }
    }

    private void AnalyzeByDayOfWeek(ProductivityInsights insights, List<(StudyRecord record, Shift shift)> data)
    {
        var dayData = new Dictionary<DayOfWeek, (double totalRvu, int dayCount)>();

        // Group records by day of week
        var recordsByDay = data.GroupBy(x => x.record.Timestamp.DayOfWeek);

        foreach (var group in recordsByDay)
        {
            var totalRvu = group.Sum(x => x.record.Rvu);
            var uniqueDays = group.Select(x => x.record.Timestamp.Date).Distinct().Count();
            dayData[group.Key] = (totalRvu, uniqueDays);
        }

        // Find best day (highest average RVU per day)
        var bestDay = dayData
            .OrderByDescending(kv => kv.Value.dayCount > 0 ? kv.Value.totalRvu / kv.Value.dayCount : 0)
            .FirstOrDefault();

        insights.BestDayOfWeek = bestDay.Key;
        insights.BestDayAvgRvu = bestDay.Value.dayCount > 0
            ? bestDay.Value.totalRvu / bestDay.Value.dayCount
            : 0;

        // Store daily breakdown for charts
        foreach (var kv in dayData)
        {
            insights.RvuByDayOfWeek[kv.Key] = kv.Value.dayCount > 0
                ? kv.Value.totalRvu / kv.Value.dayCount
                : 0;
        }
    }

    private void AnalyzeByStudyType(ProductivityInsights insights, List<(StudyRecord record, Shift shift)> data)
    {
        // Only analyze records with duration
        var recordsWithDuration = data
            .Where(x => x.record.DurationSeconds.HasValue && x.record.DurationSeconds > 0)
            .ToList();

        if (recordsWithDuration.Count == 0) return;

        var typeStats = recordsWithDuration
            .GroupBy(x => x.record.StudyType)
            .Where(g => g.Count() >= 3) // Need at least 3 samples
            .Select(g => new
            {
                StudyType = g.Key,
                AvgMinutes = g.Average(x => x.record.DurationSeconds!.Value / 60.0),
                Count = g.Count()
            })
            .ToList();

        if (typeStats.Count == 0) return;

        var fastest = typeStats.OrderBy(x => x.AvgMinutes).FirstOrDefault();
        var slowest = typeStats.OrderByDescending(x => x.AvgMinutes).FirstOrDefault();

        if (fastest != null)
        {
            insights.FastestStudyType = fastest.StudyType;
            insights.FastestAvgMinutes = fastest.AvgMinutes;
        }

        if (slowest != null && slowest != fastest)
        {
            insights.SlowestStudyType = slowest.StudyType;
            insights.SlowestAvgMinutes = slowest.AvgMinutes;
        }
    }

    private void AnalyzeByModality(ProductivityInsights insights, List<(StudyRecord record, Shift shift)> data)
    {
        var modalityRvu = new Dictionary<string, double>();

        foreach (var (record, _) in data)
        {
            var modality = ExtractModality(record.StudyType, record.Procedure);
            if (modalityRvu.ContainsKey(modality))
                modalityRvu[modality] += record.Rvu;
            else
                modalityRvu[modality] = record.Rvu;
        }

        var totalRvu = modalityRvu.Values.Sum();
        if (totalRvu == 0) return;

        var topModality = modalityRvu.OrderByDescending(kv => kv.Value).FirstOrDefault();
        insights.TopModality = topModality.Key;
        insights.TopModalityPercent = (topModality.Value / totalRvu) * 100;
    }

    private string ExtractModality(string studyType, string procedure)
    {
        var text = (studyType + " " + procedure).ToUpperInvariant();

        if (text.Contains("CT") || text.Contains("COMPUTED"))
            return "CT";
        if (text.Contains("MR") || text.Contains("MAGNETIC"))
            return "MR";
        if (text.Contains("US") || text.Contains("ULTRASOUND") || text.Contains("SONO"))
            return "US";
        if (text.Contains("XR") || text.Contains("X-RAY") || text.Contains("RADIOGRAPH"))
            return "XR";
        if (text.Contains("NM") || text.Contains("NUCLEAR") || text.Contains("PET"))
            return "NM";
        if (text.Contains("FLUORO"))
            return "FL";
        if (text.Contains("MAMMO"))
            return "MG";
        if (text.Contains("DEXA") || text.Contains("BONE DENSITY"))
            return "DX";

        return "Other";
    }

    private void GenerateRecommendations(ProductivityInsights insights)
    {
        var recommendations = new List<string>();

        // Hour recommendation
        if (insights.BestHourOfDay >= 0)
        {
            var hourStr = insights.BestHourOfDay == 0 ? "12 AM" :
                insights.BestHourOfDay < 12 ? $"{insights.BestHourOfDay} AM" :
                insights.BestHourOfDay == 12 ? "12 PM" :
                $"{insights.BestHourOfDay - 12} PM";

            recommendations.Add($"You're most productive around {hourStr}.");
        }

        // Day recommendation
        recommendations.Add($"Your best day is typically {insights.BestDayOfWeek} with {insights.BestDayAvgRvu:F1} RVU average.");

        // Speed comparison
        if (!string.IsNullOrEmpty(insights.FastestStudyType) && !string.IsNullOrEmpty(insights.SlowestStudyType))
        {
            var speedDiff = insights.SlowestAvgMinutes - insights.FastestAvgMinutes;
            if (speedDiff > 1)
            {
                recommendations.Add($"You read {insights.FastestStudyType} studies {speedDiff:F0} minutes faster than {insights.SlowestStudyType}.");
            }
        }

        // Modality recommendation
        if (!string.IsNullOrEmpty(insights.TopModality))
        {
            recommendations.Add($"{insights.TopModality} accounts for {insights.TopModalityPercent:F0}% of your RVU.");
        }

        insights.Recommendations = recommendations;
    }

    /// <summary>
    /// Generates heatmap data for calendar visualization.
    /// </summary>
    public List<HeatmapCell> GenerateHeatmap(DateTime startDate, DateTime endDate)
    {
        var cells = new List<HeatmapCell>();

        try
        {
            // Get all shifts in range
            var shifts = _database.GetAllShifts()
                .Where(s => s.ShiftStart >= startDate && s.ShiftStart <= endDate)
                .ToList();

            // Aggregate by date
            var dailyTotals = new Dictionary<DateTime, (double rvu, int studies)>();

            foreach (var shift in shifts)
            {
                var records = _database.GetRecordsForShift(shift.Id);
                var date = shift.ShiftStart.Date;

                if (dailyTotals.ContainsKey(date))
                {
                    var existing = dailyTotals[date];
                    dailyTotals[date] = (existing.rvu + records.Sum(r => r.Rvu), existing.studies + records.Count);
                }
                else
                {
                    dailyTotals[date] = (records.Sum(r => r.Rvu), records.Count);
                }
            }

            // Calculate intensity quartiles
            var rvuValues = dailyTotals.Values.Select(v => v.rvu).Where(v => v > 0).OrderBy(v => v).ToList();
            var quartiles = new double[4];

            if (rvuValues.Count >= 4)
            {
                quartiles[0] = rvuValues[(int)(rvuValues.Count * 0.25)];
                quartiles[1] = rvuValues[(int)(rvuValues.Count * 0.50)];
                quartiles[2] = rvuValues[(int)(rvuValues.Count * 0.75)];
                quartiles[3] = rvuValues.Last();
            }
            else if (rvuValues.Count > 0)
            {
                var max = rvuValues.Max();
                quartiles[0] = max * 0.25;
                quartiles[1] = max * 0.50;
                quartiles[2] = max * 0.75;
                quartiles[3] = max;
            }

            // Generate cells for all dates in range
            for (var date = startDate.Date; date <= endDate.Date; date = date.AddDays(1))
            {
                var cell = new HeatmapCell { Date = date };

                if (dailyTotals.TryGetValue(date, out var totals))
                {
                    cell.RvuTotal = totals.rvu;
                    cell.StudyCount = totals.studies;
                    cell.IntensityLevel = CalculateIntensity(totals.rvu, quartiles);
                }

                cells.Add(cell);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error generating heatmap data");
        }

        return cells;
    }

    private int CalculateIntensity(double rvu, double[] quartiles)
    {
        if (rvu <= 0) return 0;
        if (rvu <= quartiles[0]) return 1;
        if (rvu <= quartiles[1]) return 2;
        if (rvu <= quartiles[2]) return 3;
        return 4;
    }
}
