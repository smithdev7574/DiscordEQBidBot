using BidBot;
using BidBot.DataAccess;
using BidBot.EQLogs;
using BidBot.Infrastructure;
using BidBot.Items;
using BidBot.Logic;
using BidBot.Messaging;
using BidBot.Sheets;
using Discord;
using Discord.Commands;
using Discord.WebSocket;
using Microsoft.Extensions.Configuration;
using System.Text;

var builder = Host.CreateApplicationBuilder(args);
IConfiguration configuration = builder.Configuration;

var client = new DiscordSocketClient(new DiscordSocketConfig()
{
    GatewayIntents = GatewayIntents.Guilds
        | GatewayIntents.GuildMessages
        | GatewayIntents.MessageContent
        | GatewayIntents.GuildMembers
});
client.Log += Client_Log;


Task Client_Log(Discord.LogMessage arg)
{
    var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
    // Print the LogMessage with a timestamp. Include exception details when present.
    Console.WriteLine($"[{timestamp}] {arg.Source}: {arg.Severity} - {arg.Message}{(arg.Exception is null ? "" : "\n" + arg.Exception)}");
    return Task.CompletedTask;
}

//Log into the discord bot and start it...
await client.LoginAsync(Discord.TokenType.Bot, configuration["Discord:Token"]);
await client.StartAsync();

// Register IConfiguration with the DI container (optional - the host already provides it)
builder.Services.AddSingleton<IConfiguration>(configuration);
builder.Services.AddHostedService<Worker>();
builder.Services.AddHostedService<BidTimerWorker>();
builder.Services.AddSingleton(client);

//Data
builder.Services.AddSingleton<BidbotDataStore>();
builder.Services.AddSingleton<MultiItemAuctionStore>();
builder.Services.AddSingleton<GoogleSheetService>();
builder.Services.AddSingleton<EQLogStore>();

// Infrastructure services
builder.Services.AddScoped<PermissionCalculator>(); 
builder.Services.AddScoped<SlashCommandHandler>();
builder.Services.AddScoped<UserService>();

//Messaging Services
builder.Services.AddScoped<ICommunicationService, CommunicationService>();
builder.Services.AddScoped<SelectHandler>();
builder.Services.AddScoped<SelectFactory>();
builder.Services.AddSingleton<ButtonHandler>();
builder.Services.AddSingleton<ModalHandler>();
builder.Services.AddScoped<AutoCompleteHandler>();

//Item Services
builder.Services.AddSingleton<ItemListStore>();

builder.Services.AddScoped<DiscordItemFactory>();
builder.Services.AddScoped<ItemListParser>();
builder.Services.AddScoped<ItemStatsScraper>();

// Bot logic
builder.Services.AddSingleton<AuctionStore>();
builder.Services.AddScoped<BidBot.Logic.BidService>();
builder.Services.AddScoped<BidDiscordMessageFactory>();
builder.Services.AddScoped<WishlistService>();


//EQLogs
builder.Services.AddHostedService<EQLogWorker>();
builder.Services.AddScoped<ManualEQLogService>();


var services = builder.Services.BuildServiceProvider();

var host = builder.Build();
host.Run();
