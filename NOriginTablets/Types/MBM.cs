using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using OriginTablets.Support;
using System.Text.RegularExpressions;

namespace OriginTablets.Types;

public class Mbm : List<MbmEntry?>
{
  private readonly List<int> EntryIds = [];
  private Encoding SjisEncoding;

  /// <summary>
  /// Whether null entries cause the entry index to increment. False for EO3 to EO2U, true for EO5 and EON.
  /// </summary>
  private bool NullEntriesWriteIndex { get; set; }

  private long EntryTableEndAddress { get; set; } = long.MaxValue;
  private bool ParsedNonNullEntry { get; set; }

  /// <summary>
  /// This empty constructor is used for serialization.
  /// </summary>
  public Mbm()
  {
  }

  /// <summary>
  /// For loading and modifying EO text archives (MBMs).
  /// </summary>
  /// <param name="location">The location of the MBM file to load.</param>
  public Mbm(string location)
  {
    var encodingProviderInstance = CodePagesEncodingProvider.Instance;
    Encoding.RegisterProvider(encodingProviderInstance);
    SjisEncoding = Encoding.GetEncoding("shift_jis");
    using var reader = new BinaryReader(new FileStream(location, FileMode.Open));
    reader.ReadInt32(); // Always 0x00000000.
    reader.ReadInt32(); // MSG2 file identifier.
    reader.ReadInt32(); // Unknown; always 0x00010000.
    reader.ReadInt32(); // File size, excluding null entries.
    // This is supposed to be the number of entries, but it cannot be relied on for most EO games.
    reader.ReadUInt32();
    var entryTablePointer = reader.ReadUInt32();
    reader.ReadInt32(); // Unused.
    reader.ReadInt32(); // Unused.
    reader.BaseStream.Seek(entryTablePointer, SeekOrigin.Begin);
    // Since we can't rely on the number of entries, the way we check for end of the entry table
    // is a bit convoluted. We have to parse entries until we find the first non-null one,
    // and then mark its location as the end of the entry table. Until we find that non-null
    // entry, we have to set the end of the entry table to a very high value to keep the loop
    // from breaking.
    try
    {
      while (reader.BaseStream.Position < EntryTableEndAddress)
      {
        ReadEntry(reader);
      }
    }
    catch (EndOfStreamException)
    {
      Console.WriteLine($"{location} seems to be an empty MBM");
    }
  }

  private void ReadEntry(BinaryReader reader)
  {
    var entryIndex = reader.ReadInt32();
    var entryLength = reader.ReadUInt32();
    var stringPointer = reader.ReadUInt32();
    reader.ReadInt32(); // Always 0x00000000.
    // If this entry is not null, and we have not parsed a non-null entry yet,
    // mark that we have, and set the entry table end address accordingly.
    if (entryLength > 0 && stringPointer > 0 && ParsedNonNullEntry == false)
    {
      ParsedNonNullEntry = true;
      EntryTableEndAddress = stringPointer;
    }
    // If this entry is not null, add its string.
    if (entryLength > 0 && stringPointer > 0)
    {
      var storedPosition = reader.BaseStream.Position;
      reader.BaseStream.Seek(stringPointer, SeekOrigin.Begin);
      var entryData = reader.ReadBytes((int)entryLength);
      // Skip the 0xFFFF terminator.
      entryData = entryData.Take(entryData.Length - 2).ToArray();
      var controlCodes = GetControlCodes(entryData);
      Add(new MbmEntry(entryData, controlCodes));
      EntryIds.Add(entryIndex);
      reader.BaseStream.Seek(storedPosition, SeekOrigin.Begin);
    }
    // If the entry is null, add a null.
    else
    {
      Add(null!);
      // If a null entry has a non-zero index, then we're dealing with an EO5/EON MBM.
      if (entryIndex != 0)
      {
        NullEntriesWriteIndex = true;
      }
    }
  }

  /// <summary>
  /// Reads the binary representation of a string, and returns a list of all control codes found inside
  /// it. This, among other things, allows the text parser to know where and how long each control
  /// code is, so that they can be cleanly left out of text parsing.
  /// </summary>
  private List<MbmControlCode> GetControlCodes(byte[] entryData)
  {
    var result = new List<MbmControlCode>();
    var position = 0;
    while (position < entryData.Length)
    {
      var chunk = entryData.Skip(position).Take(2).ToArray();
      switch (chunk[0])
      {
        // Control code. Get what we need, update the position based on what's returned. 
        case 0x80:
        case 0xF8:
          var (controlCode, positionOffset) = GetControlCode(entryData, position);
          result.Add(controlCode);
          position += positionOffset;
          break;
        // Not a control code. Continue to the next chunk.
        default:
          position += 2;
          break;
      }
    }
    return result;
  }

  /// <summary>
  /// Constructs a MbmControlCode from the chunk and, if applicable, 
  /// </summary>
  /// <param name="entryData"></param>
  /// <param name="position"></param>
  /// <returns></returns>
  private (MbmControlCode controlCode, int positionOffset) GetControlCode(byte[] entryData, int position)
  {
    var chunk = entryData.Skip(position).Take(2).ToArray();
    var controlCode = new MbmControlCode
    {
      Type = chunk[1],
      Position = position
    };
    var positionOffset = 0;
    var stringArgument = new StringBuilder();
    var shortArguments = 0;
    switch (chunk[1])
    {
      // Int arguments
      case 0x13: // VO call (EOU, EO2U)
        controlCode.IntArguments.Add(new MbmControlCode.NumericArgument
        {
          Value = BitConverter.ToInt16(entryData.Skip(position + 2).Take(4).ToArray()),
          Position = position + 2
        });
        controlCode.IntArguments.Add(new MbmControlCode.NumericArgument
        {
          Value = BitConverter.ToInt16(entryData.Skip(position + 6).Take(4).ToArray()),
          Position = position + 6
        });
        positionOffset += 6;
        break;
      // SJIS string argument
      case 0x12: // Set telop immediate (EO2U, EO5, EON)
        var sjisStringChunk = entryData.Skip(position + 2).Take(2).ToArray();
        positionOffset += 2;
        var sjisStringChunkValue = BitConverter.ToInt16(sjisStringChunk);
        while (sjisStringChunkValue != 0x0000)
        {
          stringArgument.Append(SjisEncoding.GetString(sjisStringChunk));
          positionOffset += 2;
          sjisStringChunk = entryData.Skip(position + positionOffset).Take(2).ToArray();
          sjisStringChunkValue = BitConverter.ToInt16(sjisStringChunk);
        }
        break;
      // ASCII string argument
      case 0x1B: // VO call (EO5, EON)
        var currentByte = entryData.Skip(position + 2).Take(1);
        positionOffset += 3;
        while (currentByte.First() > 0x00)
        {
          stringArgument.Append(Encoding.ASCII.GetString([currentByte.First()]));
          currentByte = entryData.Skip(position + positionOffset).Take(1);
          positionOffset += 1;
        }
        // Adjust the position offset to account for the VO call opcode always ending on an even offset.
        if ((position + positionOffset) % 2 == 1)
        {
          positionOffset += 1;
        }
        break;
      // Short arguments
      default:
        if (MbmControlCode.ShortArgumentsByType.ContainsKey(chunk[1]))
        {
          shortArguments = MbmControlCode.ShortArgumentsByType[chunk[1]];
        }
        else
        {
          Console.WriteLine($"  Unknown control code type 0x{chunk[1]:X2}");
          positionOffset += 2;
        }
        break;
    }
    // Read however many int arguments this control code has.
    for (var i = 0; i < shortArguments; i += 1)
    {
      controlCode.ShortArguments.Add(new MbmControlCode.NumericArgument
      {
        Value = BitConverter.ToInt16(entryData.Skip(position + (2 * (i + 1))).Take(2).ToArray()),
        Position = position + 2
      });
    }
    positionOffset += (2 * (shortArguments + 1));
    controlCode.StringArgument = stringArgument.ToString().Normalize(NormalizationForm.FormKC);
    return new ValueTuple<MbmControlCode, int>(controlCode, positionOffset);
  }

  /// <summary>
  /// Save the MBM to a file.
  /// </summary>
  /// <param name="location">Where to save the MBM to.</param>
  public void WriteToFile(string location)
  {
    var encodedStrings = new List<byte[]>();
    foreach (var entry in this)
    {
      if (entry != null)
      {
        //encodedStrings.Add(GetEncodedString(entry));
      }
      else
      {
        encodedStrings.Add(new byte[0]);
      }
    }
    using (var output = new BinaryWriter(new FileStream(location, FileMode.Create), Encoding.GetEncoding("shift_jis")))
    {
      // Header writing.
      output.Write(0x0);
      output.Write((byte)0x4D);
      output.Write((byte)0x53);
      output.Write((byte)0x47);
      output.Write((byte)0x32); // MSG2 magic number.
      output.Write(0x00000100);
      output.Write(0x0); // It's probably safe to write a file size of 0. Probably.
      output.Write(Count); // Number of entries.
      output.Write(0x00000020);
      output.Write(0x0);
      output.Write(0x0);
      // How many non-null entries we've written.
      // This isn't strictly necessary for EON, but I don't see how it would hurt.
      var internalIndex = 0;
      // Our current theoretical position in the string section.
      // The initial position can be calculated as the header length plus
      // the number of entries multiplied by 0x10. Each entry is 0x10 bytes.
      var stringPosition = 0x20 + (this.Count() * 0x10);
      // Entry section writing.
      // We do a for loop instead of a foreach to make sure we catch null entries.
      foreach (var entry in encodedStrings)
      {
        // Write the individual components of an entry table entry.
        if (NullEntriesWriteIndex == true
            || (NullEntriesWriteIndex == false && entry.Length > 0))
        {
          output.Write(internalIndex);
        }
        else
        {
          output.Write(0x0);
        }
        // If the entry is null, write 0 for both length and position.
        if (entry.Length > 0)
        {
          output.Write(entry.Length);
        }
        else
        {
          output.Write(0x0);
        }
        if (entry.Length > 0)
        {
          output.Write(stringPosition);
        }
        else
        {
          output.Write(0x0);
        }
        output.Write(0x0);
        // After we've written a string position, we need to update the current position
        // for the next string.
        stringPosition += entry.Length;
        // Throwing a null object to ConvertControlCodeToBytes will cause an exception.
        // So, don't do that.
        internalIndex += 1;
      }
      // String writing.
      foreach (var entry in encodedStrings)
      {
        if (entry != null)
        {
          output.Write(entry);
        }
      }
    }
  }

  /// <summary>
  /// Takes a string with human-readable representations of control codes, and
  /// converts the instances of control codes back to control code bytes. Also
  /// converts strings back into Shift-JIS bytes.
  /// </summary>
  /// <param name="input">The string to be converted.</param>
  private byte[] GetEncodedString(string input)
  {
    // Find each unique control code in the string. For now, it should be
    // safe to assume that only control codes will use [] characters.
    var controlCodes = Regex.Matches(input, "\\[.*?\\]")
      .Select(match => match.Value)
      .Distinct();
    // We detect and remove control codes here, since otherwise parsing them and
    // encoding them is a complete nightmare. Every instance of a control code is
    // logged, and then substituted for 0xFFFF, to be replaced once we've encoded
    // the string into Shift-JIS.
    var positionsAndControlCodes = new SortedDictionary<int, string>();
    foreach (var controlCode in controlCodes)
    {
      // Log where each instance of a control code was.
      var indices = Regex.Matches(
          input, controlCode.Replace("[", "\\[").Replace("]", "\\]"))
        .Select(match => match.Index);
      // Prepare a buffer for output.
      var buffer = new StringBuilder(input);
      foreach (var index in indices)
      {
        positionsAndControlCodes.Add((index * 2), controlCode);
        // Substitute the control code strings for x instances of 0x0, where
        // x is the length of the replaced string.
        for (var controlCodeIndex = 0; controlCodeIndex < controlCode.Length; controlCodeIndex += 1)
        {
          buffer[index + controlCodeIndex] = '-';
        }
      }
      input = buffer.ToString();
    }
    // Substitute halfwidth characters with fullwidth characters, to prepare for encoding.
    var fullwidthBuffer = new StringBuilder(input);
    for (var characterIndex = 0; characterIndex < input.Length; characterIndex += 1)
    {
      var character = input[characterIndex];
      if (SJISTables.ToFullwidth.ContainsKey(character))
      {
        fullwidthBuffer[characterIndex] = SJISTables.ToFullwidth[character];
      }
    }
    input = fullwidthBuffer.ToString();
    var encodedString = Encoding.GetEncoding("shift_jis").GetBytes(input).ToList();
    // Substitute all those null characters we injected with the control code bytes.
    foreach (var controlCode in positionsAndControlCodes)
    {
      // Replace the first two placeholder null bytes with the control code.
      var splitControlCode = controlCode.Value.Replace("[", "").Replace("]", "").Split(' ');
      var upperControlCode = byte.Parse(splitControlCode[0], System.Globalization.NumberStyles.HexNumber);
      var lowerControlCode = byte.Parse(splitControlCode[1], System.Globalization.NumberStyles.HexNumber);
      encodedString[controlCode.Key] = upperControlCode;
      encodedString[controlCode.Key + 1] = lowerControlCode;
    }
    // Now that we've put all the control code bytes back in, we need to remove the
    // placeholder null bytes.
    for (var processedControlCodes = 0;
         processedControlCodes < positionsAndControlCodes.Count();
         processedControlCodes += 1)
    {
      // We know where to remove stuff from by deducting 12 bytes from the index
      // for each removal we've already performed. Every placeholder will always be 14 bytes.
      var removalStartIndex = positionsAndControlCodes.ElementAt(processedControlCodes).Key
        - (processedControlCodes * 12) + 2;
      encodedString.RemoveRange(removalStartIndex, 12);
    }
    // Add the 0xFFFF terminator.
    encodedString.Add(0xFF);
    encodedString.Add(0xFF);
    return encodedString.ToArray();
  }
}