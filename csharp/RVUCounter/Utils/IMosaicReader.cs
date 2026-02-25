namespace RVUCounter.Utils;

/// <summary>
/// Abstracts read-only Mosaic data access.
/// FlaUI scraping today; Mosaic API tomorrow.
/// </summary>
public interface IMosaicReader
{
    bool IsMosaicRunning();
    MosaicStudyData? ExtractStudyData();
    List<string> GetVisibleAccessions();
}
