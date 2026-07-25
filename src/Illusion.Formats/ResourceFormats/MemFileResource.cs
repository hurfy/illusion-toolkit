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

//THIS VERSION IS MODIFIED.
//SEE ORIGINAL CODE HERE::
//https://github.com/gibbed/Gibbed.Illusion
using Illusion.Formats.IO;

namespace Illusion.Formats.ResourceFormats;

internal class MemFileResource : IResourceFormat
{
    public uint Unk0_V4;
    public string Name = null!;
    public uint Unk1;
    public uint Unk2_V4;
    public byte[] Data = null!;

    // The envelope codec runs in the native core.

    public void Serialize(ushort version, Stream output, Endian endian) =>
        output.WriteBytes(Native.Resources.NativeResources.MemFileWrap(this, version));

    public void Deserialize(ushort version, Stream input, Endian endian) =>
        Native.Resources.NativeResources.MemFileUnwrap(
            this, version, input.ReadBytes((int)(input.Length - input.Position)));
}
