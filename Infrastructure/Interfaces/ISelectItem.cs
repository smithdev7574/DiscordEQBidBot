using System;
using System.Collections.Generic;
using System.Text;

namespace BidBot.Infrastructure
{
    public interface ISelectItem
    {
        public string Label { get; }
        public string Value { get; }

    }
}
