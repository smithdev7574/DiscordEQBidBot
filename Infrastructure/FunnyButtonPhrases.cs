using System;
using System.Collections.Generic;


namespace BidBot.Infrastructure
{

    public static class FunnyButtonPhrases
    {
        private static readonly List<string> Messages = new()
    {
        "I’m a vibe",
        "Certified button",
        "Press responsibly",
        "Chaos awaits",
        "I do stuff",
        "Button of destiny",
        "Trust the process",
        "Mildly magical",
        "I won’t bite",
        "Probably safe",
        "Mystery button",
        "I contain secrets",
        "Unleash me",
        "I dare you",
        "Button energy",
        "I’m important",
        "Press for adventure",
        "Totally normal",
        "Might be cool",
        "Proceed boldly",
        "I’m not suspicious",
        "Press me gently",
        "I’m limited edition",
        "Freshly pressed",
        "I make things happen",
        "Button vibes only",
        "I’m unpredictable",
        "Press at your own risk",
        "I’m feeling spicy",
        "I promise nothing",
        "I’m kinda awesome",
        "Button magic",
        "I’m totally safe",
        "Press to impress",
        "I’m ready to shine",
        "I do button things",
        "I’m oddly confident",
        "Press for fun",
        "I’m your destiny"
    };

        private static readonly Random _random = new();

        public static string GetRandom()
            => Messages[_random.Next(Messages.Count)];
    }

}
