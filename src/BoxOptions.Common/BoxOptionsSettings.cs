﻿using Lykke.AzureQueueIntegration;

namespace BoxOptions.Common
{
    public class BoxOptionsSettings
    {
        public SlackNotificationsSettings SlackNotifications { get; set; } = new SlackNotificationsSettings();
        public BoxOptionsApiSettings BoxOptionsApi { get; set; } = new BoxOptionsApiSettings();
    }

    public class BoxOptionsApiSettings
    {
        public ConnectionStringsSettings ConnectionStrings { get; set; } = new ConnectionStringsSettings();        
        public PricesSettingsBoxOptions PricesSettingsBoxOptions { get; set; }
        public GameManagerSettings GameManager { get; set; } = new GameManagerSettings();
        public string CoefApiUrl { get; set; }        
    }

    public class ConnectionStringsSettings
    {
        public string BoxOptionsApiStorage { get; set; }
        public string LogsConnString { get; set; }
    }
    
    public class PricesSettingsBoxOptions
    {
        public FeedSettings PrimaryFeed { get; set; }        
        public string PricesTopicName { get; set; }
        public int GraphPointsCount { get; set; }
        public int NoFeedSlackReportInSeconds { get; set; }
    }

    public class FeedSettings
    {
        public string RabbitMqConnectionString { get; set; }
        public string RabbitMqExchangeName { get; set; }        
        public string RabbitMqQueueName { get; set; }

        public int IncomingDataCheckInterval { get; set; }
        public string PricesWeekExclusionStart { get; set; }
        public string PricesWeekExclusionEnd { get; set; }
        public string[] AllowedAssets { get; set; }
    }

    public class SlackNotificationsSettings
    {
        public AzureQueueSettings AzureQueue { get; set; } = new AzureQueueSettings();
    }

    public class GameManagerSettings
    {
        public int MaxUserBuffer { get; set; } = 256;
        public string GameTopicName { get; set; } = "game.events";
    }
}
