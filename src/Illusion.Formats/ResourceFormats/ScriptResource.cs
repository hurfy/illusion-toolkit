/* Copyright (c) 2017 Rick (rick 'at' gibbed 'dot' us)
 * 
 * This software is provided 'as-is', without any express or implied
 * warranty. In no event will the authors be held liable for any damages
 * arising from the use of this software.
 * 
 * Permission is granted to anyone to use this software for any purpose,
 * including commercial applications, and to alter it and redistribute it
 * freely, subject to the following restrictions:
 * 
 * 1. The origin of this software must not be misrepresented; you must not
 *    claim that you wrote the original software. If you use this software
 *    in a product, an acknowledgment in the product documentation would
 *    be appreciated but is not required.
 * 
 * 2. Altered source versions must be plainly marked as such, and must not
 *    be misrepresented as being the original software.
 * 
 * 3. This notice may not be removed or altered from any source
 *    distribution.
 */

using Illusion.Formats.IO;

namespace Illusion.Formats.ResourceFormats;

internal class ScriptResource : IResourceFormat
{
    public string Path = null!;
    public List<ScriptData> Scripts = new List<ScriptData>();

    // The envelope codec runs in the native core.

    public void Serialize(ushort version, Stream output, Endian endian) =>
        output.WriteBytes(Native.Resources.NativeResources.ScriptWrap(this, version));

    public void Deserialize(ushort version, Stream input, Endian endian) =>
        Native.Resources.NativeResources.ScriptUnwrap(
            this, version, input.ReadBytes((int)(input.Length - input.Position)));

    // Util function to get size of bytes of all scripts
    public uint GetRawBytes()
    {
        uint TotalSize = 0;

        foreach (ScriptData script in Scripts)
        {
            TotalSize += (uint)script.Data.Length;
        }

        return TotalSize;
    }

}
