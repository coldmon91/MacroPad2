using RSoft.MacroPad.BLL.Infrasturture.Model;

namespace RSoft.MacroPad.Cli.Config;

public sealed record DeviceProfile(ushort VendorId, ushort ProductId, ProtocolType ProtocolType);
