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

internal class TextureResource : IResourceFormat
{
    public ulong NameHash;
    public byte Unknown8;
    public byte HasMIP;
    public byte[] Data = null!;

    public bool bIsDX10;

    public TextureResource()
    {
    }

    public TextureResource(ulong hash, byte hasMIP, byte[] data)
    {
        NameHash = hash;
        Unknown8 = 0;
        HasMIP = hasMIP;
        Data = data;
        bIsDX10 = false;
    }

    // The byte-level envelope runs in the native core.

    public void Serialize(ushort version, Stream output, Endian endian)
    {
        output.WriteBytes(Native.Resources.NativeResources.TextureWrap(this, version, isMip: false));
        DetermineDX10();
    }

    public void SerializeMIP(ushort version, Stream output, Endian endian) =>
        output.WriteBytes(Native.Resources.NativeResources.TextureWrap(this, version, isMip: true));

    public void Deserialize(ushort version, Stream input, Endian endian) =>
        Native.Resources.NativeResources.TextureUnwrap(
            this, version, isMip: false, input.ReadBytes((int)(input.Length - input.Position)));

    public void DeserializeMIP(ushort version, Stream input, Endian endian) =>
        Native.Resources.NativeResources.TextureUnwrap(
            this, version, isMip: true, input.ReadBytes((int)(input.Length - input.Position)));

    private void DetermineDX10()
    {
        // A payload shorter than a DDS header cannot be DX10 — reading a fixed offset from it would throw.
        if (Data.Length < 0x58)
        {
            return;
        }

        uint Magic = BitConverter.ToUInt32(Data, 0x54);

        if (Magic == 0x30315844)
        {
            bIsDX10 = true;
        }
    }

    public override string ToString()
    {
        return string.Format("Hash: {0}, Unk1: {1}, HasMIP: {2}, Size: {3}", NameHash, Unknown8, HasMIP, Data.Length);
    }
}
