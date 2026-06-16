namespace PearlPool.Proto.V2;

public partial class ShareSubmission
{
    public uint[]? ARowIndices { get; set; }
    public uint[]? BColIndices { get; set; }
    public uint K { get; set; }
    public uint NoiseRank { get; set; }
}
