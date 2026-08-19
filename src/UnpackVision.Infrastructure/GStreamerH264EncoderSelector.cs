using UnpackVision.Core;

namespace UnpackVision.Infrastructure;

/// <summary>
/// Selects only encoders proven present by the active runtime probe. The order favors
/// dedicated hardware paths, then the Windows platform encoder, then approved software.
/// GPU vendor strings and hardware model names are intentionally not consulted.
/// </summary>
public sealed class GStreamerH264EncoderSelector
{
    private static readonly MediaEncoderFamily[] PreferredFamilies =
    [
        MediaEncoderFamily.IntelQuickSync,
        MediaEncoderFamily.NvidiaNvenc,
        MediaEncoderFamily.AmdAmf,
        MediaEncoderFamily.WindowsMediaFoundation,
        MediaEncoderFamily.Software
    ];

    public MediaEncoderSelection Select(
        GStreamerRuntimeCapabilities capabilities,
        bool requireHardwareAcceleration,
        bool requireBundledRedistributionApproval = true)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        if (!capabilities.RuntimeFound || !capabilities.IsRequiredVersion)
        {
            return new MediaEncoderSelection(false, null, "The required GStreamer runtime is unavailable.");
        }
        if (!capabilities.HasAllRequiredElements)
        {
            return new MediaEncoderSelection(false, null, "One or more required GStreamer elements are unavailable.");
        }

        foreach (var family in PreferredFamilies)
        {
            var encoder = capabilities.Encoders.FirstOrDefault(candidate =>
                candidate.Family == family &&
                candidate.Available &&
                (!requireHardwareAcceleration || candidate.HardwareAccelerated) &&
                (!requireBundledRedistributionApproval || candidate.ApprovedForBundledRedistribution));
            if (encoder is not null)
            {
                return new MediaEncoderSelection(true, encoder, $"Selected tested encoder {encoder.ElementName}.");
            }
        }

        return new MediaEncoderSelection(
            false,
            null,
            requireHardwareAcceleration
                ? "No tested hardware H.264 encoder is available."
                : "No tested H.264 encoder passes the product redistribution policy.");
    }
}
