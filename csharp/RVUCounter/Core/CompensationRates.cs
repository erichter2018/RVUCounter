namespace RVUCounter.Core;

/// <summary>
/// Hardcoded compensation rates per RVU/TBWU based on time of day, day type, and role.
/// Rates vary by hour, weekday/weekend, and associate/partner status.
/// Starting April 1, 2026, TBWU becomes the primary compensation metric with its own rate table.
/// </summary>
public static class CompensationRates
{
    /// <summary>
    /// The date when TBWU becomes the primary compensation metric.
    /// Studies on or after this date use TBWU rates; studies before use RVU rates.
    /// </summary>
    public static readonly DateTime TbwuCutoverDate = new(2026, 4, 1);

    /// <summary>
    /// Returns true if the given date falls in the TBWU era (on or after April 1, 2026).
    /// </summary>
    public static bool IsTbwuEra(DateTime dateTime) => dateTime >= TbwuCutoverDate;

    // ===========================================
    // RVU RATES (used for studies before April 1, 2026)
    // ===========================================

    /// <summary>
    /// Weekday rates by role and hour (0-23).
    /// </summary>
    private static readonly Dictionary<string, int[]> WeekdayRates = new()
    {
        ["assoc"] = new int[]
        {
            // 12am, 1am, 2am, 3am, 4am, 5am, 6am, 7am, 8am, 9am, 10am, 11am
            39, 39, 41, 41, 41, 41, 39, 39, 29, 29, 29, 29,
            // 12pm, 1pm, 2pm, 3pm, 4pm, 5pm, 6pm, 7pm, 8pm, 9pm, 10pm, 11pm
            29, 29, 29, 29, 32, 32, 32, 32, 35, 37, 38, 39
        },
        ["partner"] = new int[]
        {
            // 12am, 1am, 2am, 3am, 4am, 5am, 6am, 7am, 8am, 9am, 10am, 11am
            41, 41, 43, 43, 43, 43, 41, 41, 31, 31, 31, 31,
            // 12pm, 1pm, 2pm, 3pm, 4pm, 5pm, 6pm, 7pm, 8pm, 9pm, 10pm, 11pm
            31, 31, 31, 31, 34, 34, 34, 34, 36, 37, 39, 40
        }
    };

    /// <summary>
    /// Weekend RVU rates by role and hour (0-23).
    /// </summary>
    private static readonly Dictionary<string, int[]> WeekendRates = new()
    {
        ["assoc"] = new int[]
        {
            // 12am, 1am, 2am, 3am, 4am, 5am, 6am, 7am, 8am, 9am, 10am, 11am
            41, 41, 43, 43, 43, 41, 41, 32, 32, 32, 32, 32,
            // 12pm, 1pm, 2pm, 3pm, 4pm, 5pm, 6pm, 7pm, 8pm, 9pm, 10pm, 11pm
            32, 32, 32, 32, 34, 34, 34, 34, 36, 37, 39, 40
        },
        ["partner"] = new int[]
        {
            // 12am, 1am, 2am, 3am, 4am, 5am, 6am, 7am, 8am, 9am, 10am, 11am
            43, 43, 45, 45, 45, 45, 43, 43, 34, 34, 34, 34,
            // 12pm, 1pm, 2pm, 3pm, 4pm, 5pm, 6pm, 7pm, 8pm, 9pm, 10pm, 11pm
            34, 34, 34, 34, 36, 36, 36, 36, 38, 39, 41, 42
        }
    };

    // ===========================================
    // TBWU RATES (used for studies on or after April 1, 2026)
    // ===========================================

    private static readonly Dictionary<string, double[]> TbwuWeekdayRates = new()
    {
        ["assoc"] = new double[]
        {
            // 12am,  1am,   2am,   3am,   4am,   5am,   6am,   7am,   8am,   9am,   10am,  11am
            42.77, 42.77, 44.96, 44.96, 44.96, 44.96, 42.77, 42.77, 31.80, 31.80, 31.80, 31.80,
            // 12pm,  1pm,   2pm,   3pm,   4pm,   5pm,   6pm,   7pm,   8pm,   9pm,   10pm,  11pm
            31.80, 31.80, 31.80, 31.80, 35.09, 35.09, 35.09, 35.09, 37.29, 38.38, 40.58, 41.67
        },
        ["partner"] = new double[]
        {
            // 12am,  1am,   2am,   3am,   4am,   5am,   6am,   7am,   8am,   9am,   10am,  11am
            44.96, 44.96, 47.16, 47.16, 47.16, 47.16, 44.96, 44.96, 34.00, 34.00, 34.00, 34.00,
            // 12pm,  1pm,   2pm,   3pm,   4pm,   5pm,   6pm,   7pm,   8pm,   9pm,   10pm,  11pm
            34.00, 34.00, 34.00, 34.00, 37.29, 37.29, 37.29, 37.29, 39.48, 40.58, 42.77, 43.87
        }
    };

    private static readonly Dictionary<string, double[]> TbwuWeekendRates = new()
    {
        ["assoc"] = new double[]
        {
            // 12am,  1am,   2am,   3am,   4am,   5am,   6am,   7am,   8am,   9am,   10am,  11am
            44.96, 44.96, 47.16, 47.16, 47.16, 47.16, 44.96, 44.96, 35.09, 35.09, 35.09, 35.09,
            // 12pm,  1pm,   2pm,   3pm,   4pm,   5pm,   6pm,   7pm,   8pm,   9pm,   10pm,  11pm
            35.09, 35.09, 35.09, 35.09, 37.29, 37.29, 37.29, 37.29, 39.48, 40.58, 42.77, 43.87
        },
        ["partner"] = new double[]
        {
            // 12am,  1am,   2am,   3am,   4am,   5am,   6am,   7am,   8am,   9am,   10am,  11am
            47.16, 47.16, 49.35, 49.35, 49.35, 49.35, 47.16, 47.16, 37.29, 37.29, 37.29, 37.29,
            // 12pm,  1pm,   2pm,   3pm,   4pm,   5pm,   6pm,   7pm,   8pm,   9pm,   10pm,  11pm
            37.29, 37.29, 37.29, 37.29, 39.48, 39.48, 39.48, 39.48, 41.67, 42.77, 44.96, 46.06
        }
    };

    /// <summary>
    /// Get the TBWU-specific compensation rate for a specific time and role.
    /// </summary>
    public static double GetTbwuRate(DateTime dateTime, string role)
    {
        var isWeekend = dateTime.DayOfWeek == DayOfWeek.Saturday ||
                        dateTime.DayOfWeek == DayOfWeek.Sunday;
        var roleKey = (role ?? "Associate").ToLowerInvariant().StartsWith("partner") ? "partner" : "assoc";
        var hour = dateTime.Hour;

        var rates = isWeekend ? TbwuWeekendRates : TbwuWeekdayRates;

        if (rates.TryGetValue(roleKey, out var hourlyRates))
            return hourlyRates[hour];

        return 34.0;
    }

    /// <summary>
    /// Get the TBWU-specific compensation rate for a specific time and role.
    /// </summary>
    public static double GetTbwuRate(DateTime dateTime, bool isPartner)
    {
        return GetTbwuRate(dateTime, isPartner ? "Partner" : "Associate");
    }

    /// <summary>
    /// Get the compensation rate for a specific time and role.
    /// </summary>
    /// <param name="dateTime">The date and time to get the rate for.</param>
    /// <param name="role">The role: "Associate" or "Partner" (case-insensitive).</param>
    /// <returns>The compensation rate per RVU in dollars.</returns>
    public static int GetRate(DateTime dateTime, string role)
    {
        var isWeekend = dateTime.DayOfWeek == DayOfWeek.Saturday ||
                        dateTime.DayOfWeek == DayOfWeek.Sunday;
        var roleKey = (role ?? "Associate").ToLowerInvariant().StartsWith("partner") ? "partner" : "assoc";
        var hour = dateTime.Hour; // 0-23

        var rates = isWeekend ? WeekendRates : WeekdayRates;

        if (rates.TryGetValue(roleKey, out var hourlyRates))
        {
            return hourlyRates[hour];
        }

        // Default fallback (shouldn't happen)
        return 31;
    }

    /// <summary>
    /// Get the compensation rate for a specific time and role.
    /// </summary>
    /// <param name="dateTime">The date and time to get the rate for.</param>
    /// <param name="isPartner">True if partner, false if associate.</param>
    /// <returns>The compensation rate per RVU in dollars.</returns>
    public static int GetRate(DateTime dateTime, bool isPartner)
    {
        return GetRate(dateTime, isPartner ? "Partner" : "Associate");
    }

    /// <summary>
    /// Calculate total compensation for a given RVU amount at a specific time.
    /// </summary>
    /// <param name="rvu">The RVU amount.</param>
    /// <param name="dateTime">The date and time.</param>
    /// <param name="role">The role.</param>
    /// <returns>The compensation in dollars.</returns>
    public static double CalculateCompensation(double rvu, DateTime dateTime, string role)
    {
        return rvu * GetRate(dateTime, role);
    }

    /// <summary>
    /// Calculate total compensation for records, using each record's timestamp for the rate.
    /// </summary>
    /// <param name="records">The study records with RVU and timestamp.</param>
    /// <param name="role">The role.</param>
    /// <returns>The total compensation in dollars.</returns>
    public static double CalculateTotalCompensation(IEnumerable<Models.StudyRecord> records, string role)
    {
        return records.Sum(r => CalculateCompensation(r.Rvu, r.TimeFinished ?? r.Timestamp, role));
    }

    /// <summary>
    /// Get a summary of rates for display purposes.
    /// </summary>
    /// <param name="isWeekend">True for weekend rates, false for weekday.</param>
    /// <param name="isPartner">True for partner rates, false for associate.</param>
    /// <returns>Dictionary of hour (0-23) to rate.</returns>
    public static Dictionary<int, int> GetRateSummary(bool isWeekend, bool isPartner)
    {
        var roleKey = isPartner ? "partner" : "assoc";
        var rates = isWeekend ? WeekendRates : WeekdayRates;
        var hourlyRates = rates[roleKey];

        return Enumerable.Range(0, 24).ToDictionary(h => h, h => hourlyRates[h]);
    }

    /// <summary>
    /// Get the minimum and maximum rates for a role.
    /// </summary>
    /// <param name="isPartner">True for partner, false for associate.</param>
    /// <returns>Tuple of (min, max) rates.</returns>
    public static (int Min, int Max) GetRateRange(bool isPartner)
    {
        var roleKey = isPartner ? "partner" : "assoc";
        var allRates = WeekdayRates[roleKey].Concat(WeekendRates[roleKey]);
        return (allRates.Min(), allRates.Max());
    }
}
