using Harp.Toolkit.Firmware;
using AtxmegaImage = Harp.Toolkit.Firmware.ATxmega.FirmwareImage;
using PicoImage = Harp.Toolkit.Firmware.Pico.FirmwareImage;

namespace Harp.Toolkit.Web.Devices;

/// <summary>
/// Specifies the microcontroller core a firmware image is built for.
/// </summary>
public enum FirmwareCore
{
    /// <summary>An ATxmega device, updated from an Intel HEX image through its serial bootloader.</summary>
    ATxmega,

    /// <summary>An RP2040 or RP2350 device, updated from a UF2 image through the PICOBOOT interface of its boot ROM.</summary>
    Pico
}

/// <summary>
/// Represents a firmware release file chosen by the user, with the metadata read from its name.
/// </summary>
/// <remarks>
/// Harp.Toolkit.Core creates its firmware types only from a file path, which the browser does not
/// have, so the app pairs the metadata with the image it reads from the file contents.
/// </remarks>
public abstract class FirmwareFile
{
    private protected FirmwareFile(string fileName, FirmwareMetadata? metadata)
    {
        FileName = fileName;
        Metadata = metadata;
    }

    /// <summary>
    /// Gets the name of the file.
    /// </summary>
    public string FileName { get; }

    /// <summary>
    /// Gets the device and versions the firmware is for, read from the file name, or
    /// <see langword="null"/> if the name does not follow the Harp convention and the file was
    /// read anyway. Without metadata, an update cannot check that the firmware supports the device.
    /// </summary>
    public FirmwareMetadata? Metadata { get; }

    /// <summary>
    /// Gets the core the firmware is built for.
    /// </summary>
    public abstract FirmwareCore Core { get; }

    /// <summary>
    /// Returns whether the specified file name has the extension of a firmware image this app can install.
    /// </summary>
    /// <param name="fileName">The name of the file.</param>
    /// <returns><see langword="true"/> for <c>.hex</c> and <c>.uf2</c> files; otherwise, <see langword="false"/>.</returns>
    public static bool IsSupportedExtension(string fileName) => GetCore(fileName) is not null;

    /// <summary>
    /// Reads a firmware release file, choosing the image format from its extension.
    /// </summary>
    /// <param name="fileName">
    /// The name of the file, such as <c>Behavior-fw1.2-harp1.13-hw2.0-ass0.hex</c> or
    /// <c>Pico-fw0.1-harp1.13-hw1.0-ass0.uf2</c>.
    /// </param>
    /// <param name="stream">The contents of the file.</param>
    /// <param name="allowUnknownName">
    /// <see langword="true"/> to read an image whose name does not follow the Harp convention,
    /// with no metadata; <see langword="false"/> to throw <see cref="FirmwareNameException"/> for it.
    /// </param>
    /// <returns>The firmware file.</returns>
    /// <exception cref="ArgumentException">The file is not a valid firmware image.</exception>
    /// <exception cref="FirmwareNameException">
    /// The file is a valid image, but its name does not follow the Harp convention.
    /// </exception>
    public static FirmwareFile Read(string fileName, Stream stream, bool allowUnknownName = false)
    {
        var core = GetCore(fileName) ?? throw new ArgumentException(
            "Only Intel HEX images (.hex) for ATxmega devices and UF2 images (.uf2) for Pico devices can be installed.");

        var name = Path.GetFileNameWithoutExtension(fileName);
        if (core == FirmwareCore.ATxmega && Harp.Toolkit.Firmware.SoundCard.CoprocessorFirmware.TryParseMetadata(name, out _))
        {
            throw new ArgumentException(
                "This is a SoundCard coprocessor image, which this page cannot install. Use harp.toolkit update with the main firmware image.");
        }

        // Read the image first, so only a valid image is offered for installing under any name.
        FirmwareFile Create(FirmwareMetadata? metadata) => core == FirmwareCore.Pico
            ? new PicoFirmwareFile(fileName, metadata, PicoImage.FromStream(stream))
            : new AtxmegaFirmwareFile(fileName, metadata, AtxmegaImage.FromStream(stream, AtxmegaBootloader.ImagePageSize));

        if (FirmwareMetadata.TryParse(name, out var parsed))
            return Create(parsed);

        var file = Create(null);
        if (!allowUnknownName)
        {
            throw new FirmwareNameException(
                "The name does not follow the Harp convention <device>-fw<firmware>-harp<core>-hw<hardware>-ass<assembly>, " +
                "so it does not say which device and hardware the firmware is for.");
        }

        return file;
    }

    static FirmwareCore? GetCore(string fileName) => Path.GetExtension(fileName).ToLowerInvariant() switch
    {
        ".hex" => FirmwareCore.ATxmega,
        ".uf2" => FirmwareCore.Pico,
        _ => null
    };
}

/// <summary>
/// The exception that is thrown when a valid firmware image has a name that does not follow the
/// Harp convention, so the device and hardware it is for are unknown.
/// </summary>
/// <param name="message">The message that describes the error.</param>
public sealed class FirmwareNameException(string message) : ArgumentException(message);

/// <summary>
/// Represents an Intel HEX firmware image for an ATxmega device.
/// </summary>
public sealed class AtxmegaFirmwareFile : FirmwareFile
{
    /// <summary>
    /// Initializes a new instance of the <see cref="AtxmegaFirmwareFile"/> class.
    /// </summary>
    /// <param name="fileName">The name of the file.</param>
    /// <param name="metadata">The device and versions the firmware is for.</param>
    /// <param name="image">The image to write, laid out in pages.</param>
    public AtxmegaFirmwareFile(string fileName, FirmwareMetadata? metadata, AtxmegaImage image)
        : base(fileName, metadata)
    {
        Image = image ?? throw new ArgumentNullException(nameof(image));
    }

    /// <inheritdoc/>
    public override FirmwareCore Core => FirmwareCore.ATxmega;

    /// <summary>
    /// Gets the image to write.
    /// </summary>
    public AtxmegaImage Image { get; }
}

/// <summary>
/// Represents a UF2 firmware image for an RP2040 or RP2350 device.
/// </summary>
public sealed class PicoFirmwareFile : FirmwareFile
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PicoFirmwareFile"/> class.
    /// </summary>
    /// <param name="fileName">The name of the file.</param>
    /// <param name="metadata">The device and versions the firmware is for.</param>
    /// <param name="image">The UF2 image.</param>
    public PicoFirmwareFile(string fileName, FirmwareMetadata? metadata, PicoImage image)
        : base(fileName, metadata)
    {
        Image = image ?? throw new ArgumentNullException(nameof(image));
    }

    /// <inheritdoc/>
    public override FirmwareCore Core => FirmwareCore.Pico;

    /// <summary>
    /// Gets the UF2 image.
    /// </summary>
    public PicoImage Image { get; }

    /// <summary>
    /// Gets a value indicating whether the image carries code for the RP2040.
    /// </summary>
    public bool TargetsRP2040 => Image.Families.Contains(PicoImage.FamilyRP2040);

    /// <summary>
    /// Gets a value indicating whether the image carries code for the RP2350.
    /// </summary>
    public bool TargetsRP2350 => Image.Families.Any(family =>
        family is PicoImage.FamilyRP2350ArmSecure or PicoImage.FamilyRP2350ArmNonSecure or PicoImage.FamilyRP2350RiscV);

    /// <summary>
    /// Gets the microcontrollers the image can be installed on, for display.
    /// </summary>
    public string Targets => (TargetsRP2040, TargetsRP2350) switch
    {
        (true, true) => "RP2040 or RP2350",
        (true, false) => "RP2040",
        (false, true) => "RP2350",
        _ => "no known microcontroller"
    };
}
