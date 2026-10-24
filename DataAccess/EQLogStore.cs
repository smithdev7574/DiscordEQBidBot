using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;

namespace BidBot.DataAccess
{
    public class EQLogStore
    {
        private ConcurrentQueue<string> Logs { get; set; } = new ConcurrentQueue<string>();


        public void Enqueue(string log)
        {
            Logs.Enqueue(log);
        }

        public bool Dequeue(out string result)
        {
            return Logs.TryDequeue(out result);
        }
    }
}
