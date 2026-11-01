using PCBS2Coop.Network;

namespace PCBS2Coop;

/// <summary>The game's own serialization format for a host-produced software result. Do not
/// deserialize a guest command as native object data; only the host may publish this graph.</summary>
internal static class NativeSoftwareCodec
{
    internal static byte[] Write(ComputerSoftware software)
    {
        if (software == null) throw new InvalidDataException("The PC has no software record.");
        var stream = new Il2CppSystem.IO.MemoryStream();
        try
        {
            new Il2CppSystem.Runtime.Serialization.Formatters.Binary.BinaryFormatter().Serialize(stream, software);
            if (stream.Length is < 1 or > SoftwareWorld.MaxBytes)
                throw new InvalidDataException("Software record exceeds the co-op limit.");
            return stream.ToArray();
        }
        finally { stream.Dispose(); }
    }

    internal static ComputerSoftware Read(byte[] bytes)
    {
        if (bytes == null || bytes.Length is < 1 or > SoftwareWorld.MaxBytes)
            throw new InvalidDataException("Invalid software payload size.");
        var stream = new Il2CppSystem.IO.MemoryStream(bytes);
        try
        {
            var software = new Il2CppSystem.Runtime.Serialization.Formatters.Binary.BinaryFormatter()
                .Deserialize(stream).TryCast<ComputerSoftware>();
            if (software == null || stream.Position != stream.Length)
                throw new InvalidDataException("Invalid software graph.");
            return software;
        }
        finally { stream.Dispose(); }
    }
}
