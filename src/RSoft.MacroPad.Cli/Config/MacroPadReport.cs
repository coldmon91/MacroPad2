using RSoft.MacroPad.BLL.Infrasturture.Protocol;

namespace RSoft.MacroPad.Cli.Config;

/// <summary>
/// Report whose payload bytes are set explicitly by the CLI. Used to emit the
/// device-correct wire layout for actions where the shared BLL encoding does not
/// match this hardware (mouse wheel/click). See doc/protocol-6key-1knob.md.
/// </summary>
internal sealed class MacroPadReport : Report
{
    public MacroPadReport(byte reportId)
    {
        // Data is inherited as a zeroed 64+ byte payload; callers set bytes via the Data getter.
        ReportId = reportId;
    }
}
