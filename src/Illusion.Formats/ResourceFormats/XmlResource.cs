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

internal class XmlResource : IResourceFormat
{
    public string Tag = null!;
    public bool Unk1;
    public string Name = null!;
    public bool Unk3;

    public string Content = null!;

    public bool bFailedToDecompile = false;

    // The binary codecs run in the native core; the .xml text rendering/parsing stays managed
    // (XmlText) — the split the port settled on: bytes are the core's, text is the toolkit's.

    public void Serialize(ushort version, Stream output, Endian endian)
    {
        var model = new Native.Model.XmlDocumentModel
        {
            Tag = this.Tag,
            Unk1 = (byte)(this.Unk1 ? 1 : 0),
            Name = this.Name,
            Unk3 = (byte)(this.Unk3 ? 1 : 0),
            FailedToDecompile = (byte)(this.bFailedToDecompile ? 1 : 0),
        };

        if (this.Unk3)
        {
            model.Nodes = Native.Resources.XmlText.ParseCodec1(this.Content);
        }
        else if (this.bFailedToDecompile)
        {
            model.RawPayload = File.ReadAllBytes(this.Content);
        }
        else
        {
            model.Nodes = Native.Resources.XmlText.ParseCodec0(this.Content);
        }

        output.WriteBytes(Native.Resources.NativeResources.XmlEncode(version, model));
    }

    public void Deserialize(ushort version, Stream input, Endian endian)
    {
        long start = input.Position;
        byte[] data = input.ReadBytes((int)(input.Length - start));
        Native.Model.XmlDocumentModel model = Native.Resources.NativeResources.XmlDecode(version, data);

        this.Tag = model.Tag;
        this.Unk1 = model.Unk1 != 0;
        this.Name = model.Name;
        this.Unk3 = model.Unk3 != 0;

        if (model.Unk3 != 0)
        {
            this.Content = Native.Resources.XmlText.RenderCodec1(model.Nodes);
            return;
        }

        if (model.FailedToDecompile == 0)
        {
            try
            {
                this.Content = Native.Resources.XmlText.RenderCodec0(model.Nodes);
                return;
            }
            catch (Exception ex) when (ex is FormatException or InvalidOperationException
                or KeyNotFoundException or NotSupportedException)
            {
                System.Diagnostics.Debug.WriteLine("XmlResource render failed: " + ex.Message);
            }
        }

        // Lossless passthrough: leave the stream positioned at the codec zone, exactly
        // where the managed twin leaves it, so the extractor writes the raw payload.
        bFailedToDecompile = true;
        input.Position = start + (data.Length - model.RawPayload.Length);
    }

}
