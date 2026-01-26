using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RVUCounter.Models;
using Serilog;

namespace RVUCounter.Logic;

/// <summary>
/// Handles anonymous team stats synchronization via free JSON storage (jsonblob.com).
/// Privacy-first design: only aggregate stats, no identifying information.
/// </summary>
public class TeamSyncService : IDisposable
{
    private readonly HttpClient _httpClient;
    private const string JsonBlobApiUrl = "https://jsonblob.com/api/jsonBlob";
    private const int StaleThresholdMinutes = 5; // Consider member offline after 5 min

    public TeamSyncService()
    {
        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(10)
        };
        _httpClient.DefaultRequestHeaders.Add("Accept", "application/json");
    }

    /// <summary>
    /// Generates a 6-character team code.
    /// </summary>
    public static string GenerateTeamCode()
    {
        const string chars = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"; // Avoid confusing chars
        var random = RandomNumberGenerator.Create();
        var bytes = new byte[6];
        random.GetBytes(bytes);
        return new string(bytes.Select(b => chars[b % chars.Length]).ToArray());
    }

    /// <summary>
    /// Generates a random anonymous ID (not tied to machine/user).
    /// </summary>
    public static string GenerateAnonymousId()
    {
        var random = RandomNumberGenerator.Create();
        var bytes = new byte[16];
        random.GetBytes(bytes);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    /// <summary>
    /// Creates a new team and returns the storage URL.
    /// </summary>
    public async Task<(bool success, string teamCode, string storageUrl, string error)> CreateTeamAsync()
    {
        try
        {
            var teamCode = GenerateTeamCode();
            var teamData = new TeamData
            {
                TeamCode = teamCode,
                CreatedAt = DateTime.UtcNow,
                Members = new List<TeamMemberStats>()
            };

            var json = JsonSerializer.Serialize(teamData);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            // Create a new blob on jsonblob.com (no auth required)
            var response = await _httpClient.PostAsync(JsonBlobApiUrl, content);

            if (response.IsSuccessStatusCode)
            {
                // jsonblob.com returns the blob URL in the Location header
                var locationHeader = response.Headers.Location;
                if (locationHeader != null)
                {
                    var storageUrl = locationHeader.ToString();

                    // If it's a relative path, prepend the base URL
                    if (storageUrl.StartsWith("/"))
                    {
                        storageUrl = "https://jsonblob.com" + storageUrl;
                    }

                    Log.Information("Created team {TeamCode} at {StorageUrl}", teamCode, storageUrl);
                    return (true, teamCode, storageUrl, "");
                }
                else
                {
                    Log.Error("Failed to create team: No Location header in response");
                    return (false, "", "", "Failed to create team: No URL returned");
                }
            }
            else
            {
                var error = await response.Content.ReadAsStringAsync();
                Log.Error("Failed to create team: {StatusCode} - {Error}", response.StatusCode, error);
                return (false, "", "", $"Failed to create team: {response.StatusCode}");
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error creating team");
            return (false, "", "", ex.Message);
        }
    }

    /// <summary>
    /// Joins an existing team by looking up the team code.
    /// Note: In a real implementation, you'd have a lookup service.
    /// For simplicity, team codes are stored in the bin name.
    /// </summary>
    public Task<(bool success, string storageUrl, string error)> JoinTeamAsync(string teamCode)
    {
        // For this implementation, users share both code and storage URL
        // The storage URL is generated when creating the team
        // This is a simplified approach - in production you'd use a lookup service

        // Validate team code format
        if (string.IsNullOrEmpty(teamCode) || teamCode.Length != 6)
        {
            return Task.FromResult((false, "", "Invalid team code format"));
        }

        // In practice, the full storage URL should be shared
        // For now, return success if code looks valid
        return Task.FromResult((true, "", "Please enter the full team URL shared by your team creator"));
    }

    /// <summary>
    /// Updates team stats with your current data.
    /// </summary>
    public async Task<bool> UpdateStatsAsync(string storageUrl, TeamMemberStats myStats)
    {
        if (string.IsNullOrEmpty(storageUrl)) return false;

        try
        {
            // Get current team data
            var response = await _httpClient.GetAsync(storageUrl);
            if (!response.IsSuccessStatusCode)
            {
                Log.Warning("Failed to fetch team data: {StatusCode}", response.StatusCode);
                return false;
            }

            var json = await response.Content.ReadAsStringAsync();
            var teamData = JsonSerializer.Deserialize<TeamData>(json);

            if (teamData == null) return false;

            // Update or add our stats
            myStats.Timestamp = DateTime.UtcNow;
            var existingIndex = teamData.Members.FindIndex(m => m.Id == myStats.Id);
            if (existingIndex >= 0)
            {
                teamData.Members[existingIndex] = myStats;
            }
            else
            {
                teamData.Members.Add(myStats);
            }

            // Remove stale members (not updated in 5 minutes)
            var cutoff = DateTime.UtcNow.AddMinutes(-StaleThresholdMinutes);
            teamData.Members.RemoveAll(m => m.Timestamp < cutoff);

            // Update the blob
            var updateJson = JsonSerializer.Serialize(teamData);
            var updateContent = new StringContent(updateJson, Encoding.UTF8, "application/json");
            var updateResponse = await _httpClient.PutAsync(storageUrl, updateContent);

            return updateResponse.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error updating team stats");
            return false;
        }
    }

    /// <summary>
    /// Fetches team stats.
    /// </summary>
    public async Task<(List<TeamMemberStats> members, bool success)> GetTeamStatsAsync(string storageUrl)
    {
        if (string.IsNullOrEmpty(storageUrl))
            return (new List<TeamMemberStats>(), false);

        try
        {
            var response = await _httpClient.GetAsync(storageUrl);
            if (!response.IsSuccessStatusCode)
            {
                return (new List<TeamMemberStats>(), false);
            }

            var json = await response.Content.ReadAsStringAsync();
            var teamData = JsonSerializer.Deserialize<TeamData>(json);

            if (teamData == null)
                return (new List<TeamMemberStats>(), false);

            // Filter to active members only (updated within threshold)
            var cutoff = DateTime.UtcNow.AddMinutes(-StaleThresholdMinutes);
            var activeMembers = teamData.Members
                .Where(m => m.Timestamp >= cutoff && m.ShiftActive)
                .ToList();

            return (activeMembers, true);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error fetching team stats");
            return (new List<TeamMemberStats>(), false);
        }
    }

    /// <summary>
    /// Prepares display data with positions on a number line.
    /// Returns (displayList, minValue, maxValue).
    /// </summary>
    /// <param name="members">Team member stats</param>
    /// <param name="myId">Current user's anonymous ID</param>
    /// <param name="useRate">True = use RVU/hour, False = use Total RVU</param>
    public static (List<TeamMemberDisplay> members, double minValue, double maxValue) PrepareDisplayData(
        List<TeamMemberStats> members,
        string myId,
        bool useRate = true)
    {
        if (members.Count == 0)
            return (new List<TeamMemberDisplay>(), 0, 0);

        // Select the appropriate value based on mode
        Func<TeamMemberStats, double> getValue = useRate
            ? m => m.RvuPerHour
            : m => m.TotalRvu;

        var minVal = members.Min(getValue);
        var maxVal = members.Max(getValue);

        // Ensure we have a range (avoid division by zero)
        var range = maxVal - minVal;
        if (range < 0.1) range = Math.Max(maxVal, 1);

        // Pad min/max slightly for visual spacing
        var displayMin = Math.Max(0, minVal - range * 0.05);
        var displayMax = maxVal + range * 0.05;
        var displayRange = displayMax - displayMin;
        if (displayRange < 0.1) displayRange = 1;

        var displayList = members.Select(m => new TeamMemberDisplay
        {
            TotalRvu = m.TotalRvu,
            RvuPerHour = m.RvuPerHour,
            DisplayValue = getValue(m),
            PositionPercent = (getValue(m) - displayMin) / displayRange,
            IsYou = m.Id == myId
        }).ToList();

        // Sort by display value (ascending) for consistent positioning
        displayList = displayList.OrderBy(m => m.DisplayValue).ToList();

        return (displayList, displayMin, displayMax);
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }
}
