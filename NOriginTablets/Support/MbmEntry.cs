using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace OriginTablets.Support;

public class MbmEntry(byte[] data, List<MbmControlCode> controlCodes)
{
  private byte[] Data { get; } = data;
  private List<MbmControlCode> ControlCodes { get; } = controlCodes;

  public delegate string Formatter(MbmControlCode controlCode);
  
  public Formatter NoArgsFormatter { get; set; } = DefaultNoArgsControlCodeFormatter;
  public Formatter ShortArgsFormatter { get; set; } = DefaultShortArgsControlCodeFormatter;
  public Formatter IntArgsFormatter { get; set; } = DefaultIntArgsControlCodeFormatter;
  public Formatter StringArgFormatter { get; set; } = DefaultStringArgControlCodeFormatter;

  public string GetString()
  {
    var buffer = new StringBuilder();
    var position = 0;
    while (position < Data.Length)
    {
      // Insert control code placeholders if we're at the correct position.
      if (controlCodes.Any(cc => cc.Position == position))
      {
        var controlCode = ControlCodes.First(cc => cc.Position == position);
        if (controlCode.HasShortArguments)
        {
          buffer.Append(ShortArgsFormatter(controlCode));
          position += 2 + (2 * controlCode.ShortArguments.Count);
        }
        else if (controlCode.HasIntArguments)
        {
          buffer.Append(IntArgsFormatter(controlCode));
          position += 2 + (4 * controlCode.IntArguments.Count);
        }
        else if (controlCode.HasStringArgument)
        {
          buffer.Append(StringArgFormatter(controlCode));
          var lengthMultiplier = controlCode.Type == 0x12 // 0x12 is telop set (immediate)
            ? 2 // SJIS
            : 1; // ASCII
          position += 2 
                      + controlCode.StringArgument.Length * lengthMultiplier // The string
                      + 2; // Double-width null terminator
          // For ASCII arguments, remember to pad the position back to an even offset.
          if (position % 2 == 1)
          {
            position += 1;
          }
        }
        else
        {
          buffer.Append(NoArgsFormatter(controlCode));
          position += 2;
        }
      }
      else
      {
        buffer.Append(Encoding.GetEncoding("shift_jis")
          .GetString(Data.Skip(position).Take(2).ToArray()));
        position += 2;
      }
    }
    return buffer.ToString().Normalize(NormalizationForm.FormKC);
  }

  private static string DefaultNoArgsControlCodeFormatter(MbmControlCode controlCode)
  {
    return $"[{controlCode.Type:X2}]";
  }

  private static string DefaultShortArgsControlCodeFormatter(MbmControlCode controlCode)
  {
    return $"[{controlCode.Type:X2}: {string.Join(", ", controlCode.ShortArguments
      .Select(ia => $"0x{ia.Value:X4}"))}]";
  }

  private static string DefaultIntArgsControlCodeFormatter(MbmControlCode controlCode)
  {
    return $"[{controlCode.Type:X2}: {string.Join(", ", controlCode.IntArguments
      .Select(ia => $"0x{ia.Value:X4}"))}]";
  }

  private static string DefaultStringArgControlCodeFormatter(MbmControlCode controlCode)
  {
    return $"[{controlCode.Type:X2}: \"{controlCode.StringArgument}\"]";
  }
}