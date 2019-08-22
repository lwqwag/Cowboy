using System;

namespace Cowboy.Sockets
{
    public class FrameBuilder : IFrameBuilder
    {
        public FrameBuilder(IFrameEncoder encoder, IFrameDecoder decoder)
        {
            Encoder = encoder ?? throw new ArgumentNullException("encoder");
            Decoder = decoder ?? throw new ArgumentNullException("decoder");
        }

        public IFrameEncoder Encoder { get; private set; }
        public IFrameDecoder Decoder { get; private set; }
    }
}
