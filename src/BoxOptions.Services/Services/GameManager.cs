﻿using BoxOptions.Common;
using BoxOptions.Common.Interfaces;
using BoxOptions.Core;
using BoxOptions.Core.Models;
using BoxOptions.Services.Interfaces;
using BoxOptions.Services.Models;
using Common.Log;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using WampSharp.V2.Realm;


namespace BoxOptions.Services
{
    public class GameManager : IGameManager, IDisposable
    {
        const int NPriceIndex = 15; // Number of columns hardcoded
        const int NTimeIndex = 8;   // Number of rows hardcoded
        const int CoeffMonitorTimerInterval = 1000; // Coeff cache update interval (milliseconds)


        #region Vars
        /// <summary>
        /// Coefficient Calculator Request Semaphore
        /// Mutual Exclusion Process
        /// </summary>
        static System.Threading.SemaphoreSlim coeffCalculatorSemaphoreSlim = new System.Threading.SemaphoreSlim(1, 1);
        /// <summary>
        /// Process AssetQuote Received Semaphore.
        /// Mutual Exclusion Process
        /// </summary>
        static System.Threading.SemaphoreSlim quoteReceivedSemaphoreSlim = new System.Threading.SemaphoreSlim(1, 1);

        static int MaxUserBuffer = 128;
        static object CoeffCacheLock = new object();
        static object BetCacheLock = new object();


        string GameManagerId;

        /// <summary>
        /// Ongoing Bets Cache
        /// </summary>
        List<GameBet> betCache;
        /// <summary>
        /// Users Cache
        /// </summary>
        List<UserState> userList;

        /// <summary>
        /// Database Object
        /// </summary>
        private readonly IGameDatabase database;
        /// <summary>
        /// CoefficientCalculator Object
        /// </summary>
        private readonly ICoefficientCalculator calculator;
        /// <summary>
        /// QuoteFeed Object
        /// </summary>
        private readonly IAssetQuoteSubscriber quoteFeed;
        /// <summary>
        /// WAMP Realm Object
        /// </summary>
        private readonly IWampHostedRealm wampRealm;
        private readonly IMicrographCache micrographCache;
        /// <summary>
        /// BoxSize configuration
        /// </summary>
        private readonly IBoxConfigRepository boxConfigRepository;
        /// <summary>
        /// User Log Repository
        /// </summary>
        private readonly ILogRepository logRepository;
        /// <summary>
        /// Application Log Repository
        /// </summary>
        private readonly ILog appLog;
        /// <summary>
        /// Settings
        /// </summary>
        private readonly BoxOptionsSettings settings;
        /// <summary>
        /// Last Prices Cache
        /// </summary>
        private Dictionary<string, PriceCache> assetCache;

        /// <summary>
        /// Box configuration
        /// </summary>
        List<BoxSize> defaultBoxConfig;

        Queue<string> appLogInfoQueue = new Queue<string>();

        System.Threading.Timer CoeffMonitorTimer = null;
        bool isDisposing = false;
        #endregion

        #region Constructor
        public GameManager(BoxOptionsSettings settings, IGameDatabase database, ICoefficientCalculator calculator,
            IAssetQuoteSubscriber quoteFeed, IWampHostedRealm wampRealm, IMicrographCache micrographCache, IBoxConfigRepository boxConfigRepository, ILogRepository logRepository, ILog appLog)
        {
            this.database = database;
            this.calculator = calculator;
            this.quoteFeed = quoteFeed;
            this.settings = settings;
            this.logRepository = logRepository;
            this.appLog = appLog;
            this.wampRealm = wampRealm;
            this.boxConfigRepository = boxConfigRepository;
            this.micrographCache = micrographCache;

            if (this.settings != null && this.settings.BoxOptionsApi != null && this.settings.BoxOptionsApi.GameManager != null)
                MaxUserBuffer = this.settings.BoxOptionsApi.GameManager.MaxUserBuffer;

            GameManagerId = Guid.NewGuid().ToString();
            userList = new List<UserState>();
            betCache = new List<GameBet>();
            assetCache = new Dictionary<string, PriceCache>();

            this.quoteFeed.MessageReceived += QuoteFeed_MessageReceived;

            defaultBoxConfig = null;
        }


        #endregion

        #region Methods
        DateTime LastErrorDate = DateTime.MinValue;
        string LastErrorMessage = "";
        private async void ReportError(string process, Exception ex)
        {
            Exception innerEx;
            if (ex.InnerException != null)
                innerEx = ex.InnerException;
            else
                innerEx = ex;
                        
            bool LogError;
            if (LastErrorMessage != innerEx.Message)
            {
                LogError = true;
            }
            else
            {
                if (DateTime.UtcNow > LastErrorDate.AddMinutes(1))
                    LogError = true;
                else
                    LogError = false;
            }


            if (LogError)
            {
                LastErrorMessage = innerEx.Message;
                LastErrorDate = DateTime.UtcNow;
                await appLog.WriteErrorAsync("GameManager", process, null, innerEx);
                //Console.WriteLine("Logged: {0}", innerEx.Message);
            }
        }

        private void InitializeCoefCalc()
        {
            BoxSize[] calculatedParams = CalculatedBoxes(defaultBoxConfig, micrographCache);
            try
            {
                Task t = CoeffCalculatorChangeBatch(GameManagerId, calculatedParams);
                t.Wait();
            }
            catch (Exception ex) { ReportError("InitializeCoefCalc",ex); }
            LoadCoefficientCache();

            StartCoefficientCacheMonitor();
        }
                
        private List<BoxSize> LoadBoxParameters()
        {
            Task<IEnumerable<BoxSize>> t = boxConfigRepository.GetAll();
            t.Wait();

            List<BoxSize> boxConfig = t.Result.ToList();
            List<BoxSize> AssetsToAdd = new List<BoxSize>();

            List<string> AllAssets = settings.BoxOptionsApi.PricesSettingsBoxOptions.PrimaryFeed.AllowedAssets.ToList();
            
            string[] DistictAssets = AllAssets.Distinct().ToArray();
            // Validate Allowed Assets
            foreach (var item in DistictAssets)
            {

                // If database does not contain allowed asset then add it
                if (!boxConfig.Select(config => config.AssetPair).Contains(item))
                {
                    // Check if it was not added before to avoid duplicates
                    if (!AssetsToAdd.Select(config => config.AssetPair).Contains(item))
                    {
                        // Add default settings
                        AssetsToAdd.Add(new BoxSize()
                        {
                            AssetPair = item,
                            BoxesPerRow = 7,
                            BoxHeight = 7,
                            BoxWidth = 0.00003,
                            TimeToFirstBox = 4
                        });
                    }
                }
            }
            if (AssetsToAdd.Count > 0)
            {
                boxConfigRepository.InsertManyAsync(AssetsToAdd);
                boxConfig.AddRange(AssetsToAdd);
            }

            List<BoxSize> retval = new List<BoxSize>();
            foreach (var item in DistictAssets)
            {
                var box = boxConfig.Where(bx => bx.AssetPair == item).FirstOrDefault();
                retval.Add(new BoxSize()
                {
                    AssetPair = box.AssetPair,
                    BoxesPerRow = box.BoxesPerRow,
                    BoxHeight = box.BoxHeight,
                    BoxWidth = box.BoxWidth,
                    TimeToFirstBox = box.TimeToFirstBox
                });

            }

            return retval;
        }

        /// <summary>
        /// Calculate Box Width acording to BoxSize
        /// </summary>
        /// <param name="boxConfig"></param>
        /// <param name="priceCache"></param>
        /// <returns></returns>
        private BoxSize[] CalculatedBoxes(List<BoxSize> boxConfig, IMicrographCache priceCache)
        {
            var gdata = priceCache.GetGraphData();

            // Only send pairs with graph data
            var filtered = from c in boxConfig
                           where gdata.ContainsKey(c.AssetPair)
                           select c;

            // Calculate BoxWidth according to average prices
            // BoxWidth = average(asset.midprice) * Box.PriceSize from database
            BoxSize[] retval = (from c in filtered
                                select new BoxSize()
                                {
                                    AssetPair = c.AssetPair,
                                    BoxesPerRow = c.BoxesPerRow,
                                    BoxHeight = c.BoxHeight,
                                    TimeToFirstBox = c.TimeToFirstBox,
                                    BoxWidth = gdata[c.AssetPair].Average(price => price.MidPrice()) * c.BoxWidth
                                }).ToArray();
            return retval;
        }

        private GameBet[] GetRunningBets()
        {
            GameBet[] retval;
            lock (BetCacheLock)
            {
                retval = new GameBet[betCache.Count];
                betCache.CopyTo(retval);
            }
            return retval;
        }

        #region Coefficient Cache Monitor
        Dictionary<string, string> CoefficientCache;

        private void LoadCoefficientCache()
        {            
            try
            {
                string[] assets = defaultBoxConfig.Select(b => b.AssetPair).ToArray();
                Task<Dictionary<string, string>> t = CoeffCalculatorRequestBatch(GameManagerId, assets);
                t.Wait();

                lock (CoeffCacheLock)
                {
                    CoefficientCache = t.Result;
                }
            }
            catch (Exception ex) { ReportError("LoadCoefficientCache", ex); }
        }

        private string GetCoefficients(string assetPair)
        {
            string retval = "";
            lock (CoeffCacheLock)
            {
                retval = CoefficientCache[assetPair];
            }
            return retval;
        }

        private void StartCoefficientCacheMonitor()
        {
            CoeffMonitorTimer = new System.Threading.Timer(new System.Threading.TimerCallback(CoeffMonitorTimerCallback), null, CoeffMonitorTimerInterval, -1);

        }
        private void CoeffMonitorTimerCallback(object status)
        {
            CoeffMonitorTimer.Change(-1, -1);
            
            LoadCoefficientCache();
            
            if (!isDisposing)
                CoeffMonitorTimer.Change(CoeffMonitorTimerInterval, -1);
        }

        private async Task<string> CoeffCalculatorChangeBatch(string userId, BoxSize[] boxes)
        {
            await coeffCalculatorSemaphoreSlim.WaitAsync();
            try
            {
                string res = "EMPTY BOXES";
                                
                foreach (var box in boxes)
                {
                    // Change calculator parameters for current pair with User parameters                    
                    res = await calculator.ChangeAsync(userId, box.AssetPair, Convert.ToInt32(box.TimeToFirstBox), Convert.ToInt32(box.BoxHeight), box.BoxWidth, NPriceIndex, NTimeIndex);
                    if (res != "OK")
                        throw new InvalidOperationException(res);
                }                
                return res;
            }
            finally { coeffCalculatorSemaphoreSlim.Release(); }


        }
        /// <summary>
        /// Performs a Coefficient Request to CoeffCalculator object
        /// </summary>
        /// <param name="userId">User Id</param>
        /// <param name="pair">Instrument</param>
        /// <param name="timeToFirstOption">Time to first option</param>
        /// <param name="optionLen">Option Length</param>
        /// <param name="priceSize">Price Size</param>
        /// <param name="nPriceIndex">NPrice Index</param>
        /// <param name="nTimeIndex">NTime Index</param>
        /// <returns>CoeffCalc result</returns>
        private async Task<Dictionary<string, string>> CoeffCalculatorRequestBatch(string userId, string[] assetPairs)
        {            
            //Activate Mutual Exclusion Semaphor
            await coeffCalculatorSemaphoreSlim.WaitAsync();
            try
            {
                Dictionary<string, string> retval = new Dictionary<string, string>();                
                foreach (var asset in assetPairs)
                {
                    string res = await calculator.RequestAsync(userId, asset);
                    retval.Add(asset, res);
                }
                return retval;
            }
            finally { coeffCalculatorSemaphoreSlim.Release(); }

        }

        #endregion

        #region User Methods

        private BoxSize[] InitializeUser(string userId)
        {
            UserState userState = GetUserState(userId);

            //
            List<BoxSize> boxConfig = LoadBoxParameters();

            if (defaultBoxConfig == null)
            {
                defaultBoxConfig = (from c in boxConfig
                                    select new BoxSize()
                                    {
                                        AssetPair = c.AssetPair,
                                        BoxesPerRow = c.BoxesPerRow,
                                        BoxHeight = c.BoxHeight,
                                        BoxWidth = c.BoxWidth,
                                        TimeToFirstBox = c.TimeToFirstBox
                                    }).ToList();
                InitializeCoefCalc();

            }

            // Return Calculate Price Sizes
            BoxSize[] retval = CalculatedBoxes(boxConfig, micrographCache);
            return retval;
        }

        /// <summary>
        /// Finds user object in User cache or loads it from DB if not in cache
        /// Opens Wamp Topic for User Client
        /// </summary>
        /// <param name="userId">User Id</param>
        /// <returns>User Object</returns>
        private UserState GetUserState(string userId)
        {
            var ulist = from u in userList
                        where u.UserId == userId
                        select u;
            if (ulist.Count() > 1)
                throw new InvalidOperationException("User State List has duplicate entries");

            UserState current = ulist.FirstOrDefault();
            if (current == null)
            {
                // UserState not in current cache,
                // download it from database
                Task<UserState> t = LoadUserStateFromDb(userId);                               
                t.Wait();
                current = t.Result;

                // Assing WAMP realm to user
                current.StartWAMP(wampRealm, this.settings.BoxOptionsApi.GameManager.GameTopicName);

                // keep list size to maxbuffer
                if (userList.Count >= MaxUserBuffer)
                {
                    var OlderUser = (from u in userList
                                     orderby u.LastChange
                                     select u).FirstOrDefault();

                    if (OlderUser != null)
                    {
                        // Check if user does not have running bets
                        //var userOpenBets = from b in betCache
                        //                   where b.UserId == OlderUser.UserId
                        //                   select b;
                        var userOpenBets = from b in OlderUser.OpenBets
                                           where b.BetStatus == BetStates.Waiting || b.BetStatus == BetStates.OnGoing
                                           select b;

                        // No running bets. Kill user
                        if (userOpenBets.Count() == 0)
                        {
                            // Remove user from cache
                            userList.Remove(OlderUser);
                            
                            // Dispose user
                            OlderUser.Dispose();
                        }
                    }
                }
                // add it to cache
                userList.Add(current);
            }
            return current;
        }

        /// <summary>
        /// Loads user object from DB
        /// </summary>
        /// <param name="userId">User Id</param>
        /// <returns>User Object</returns>
        private async Task<UserState> LoadUserStateFromDb(string userId)
        {
            //await MutexTestAsync();
            //MutexTest();
            
            // Database object fetch
            UserState retval = await database.LoadUserState(userId);            

            if (retval == null)
            {
                // UserState not in database
                // Create new
                retval = new UserState(userId);                
                //retval.SetBalance(40.50m);
                // Save it to Database
                await database.SaveUserState(retval);

            }
            else
            {                
                // Load User Parameters
                //var userParameters = await database.LoadUserParameters(userId);
                //retval.LoadParameters(userParameters);

                // TODO: Load User Bets                
                //var bets = await database.LoadGameBets(userId, (int)GameBet.BetStates.OnGoing);
                //retval.LoadBets(bets);
            }
            
            return retval;
        }
                
        /// <summary>
        /// Sets User status, creates an UserHistory entry and saves user to DB
        /// </summary>
        /// <param name="user">User Object</param>
        /// <param name="status">New Status</param>
        /// <param name="message">Status Message</param>
        private void SetUserStatus(UserState user, GameStatus status, string message = null)
        {
            var hist = user.SetStatus((int)status, message);
            // Save User
            database.SaveUserState(user);
            // Save user History
            database.SaveUserHistory(hist);
        }

        #endregion

        #region Game Logic

        private GameBet PlaceNewBet(string userId, string assetPair, string box, decimal bet, out string message)
        {
            message = "Placing Bet";

            // Get user state
            UserState userState = GetUserState(userId);

            // Validate balance
            if (bet > userState.Balance)
            {
                message = "User has no balance for the bet.";
                return null;
            }

            // TODO: Get Box from... somewhere            
            Box boxObject = Box.FromJson(box);


            // Get Current Coeffs for Game's Assetpair
            var assetConfig = defaultBoxConfig.Where(b => b.AssetPair == assetPair).FirstOrDefault();
            if (assetConfig == null)
            {
                message = $"Box Size parameters are not set for Asset Pair[{ assetPair}].";
                return null;
            }

            // Place Bet            
            GameBet newBet = userState.PlaceBet(boxObject, assetPair, bet, assetConfig);
            newBet.TimeToGraphReached += Bet_TimeToGraphReached;
            newBet.TimeLenghFinished += Bet_TimeLenghFinished;

            // Update user balance
            userState.SetBalance(userState.Balance - bet);

            // Run bet
            newBet.StartWaitTimeToGraph();

            // Async Save to Database
            Task.Run(() =>
            {
                // Save bet to DB                
                database.SaveGameBet(newBet);

                // Set Status, saves User to DB            
                SetUserStatus(userState, GameStatus.BetPlaced, $"BetPlaced[{boxObject.Id}]. Asset:{assetPair}  Bet:{bet} Balance:{userState.Balance}");
            });

            message = "OK";
            return newBet;
        }

        /// <summary>
        /// Checks Bet WIN agains given parameters
        /// </summary>
        /// <param name="bet"></param>
        /// <param name="dCurrentPrice"></param>
        /// <param name="dPreviousPrice"></param>
        /// <returns>TRUE if WIN</returns>
        private bool CheckWinOngoing(GameBet bet, double dCurrentPrice, double dPreviousPrice)
        {
            decimal currentPrice = Convert.ToDecimal(dCurrentPrice);
            decimal previousPrice = Convert.ToDecimal(dPreviousPrice);

            double currentDelta = (double)currentPrice - dCurrentPrice;
            double previousDelta = (double)previousPrice - dPreviousPrice;

            if (currentDelta > 0.000001 || currentDelta < -0.000001)
                appLog.WriteWarningAsync("GameManager", "CheckWinOngoing", "", $"Double to Decimal conversion Fail! CurrDelta={currentDelta} double:{dCurrentPrice} decimal:{currentPrice}");
            if (previousDelta > 0.000001 || previousDelta < -0.000001)
                appLog.WriteWarningAsync("GameManager", "CheckWinOngoing", "", $"Double to Decimal conversion Fail! PrevDelta={previousDelta} double:{dPreviousPrice} decimal:{previousPrice}");


            if ((currentPrice > bet.Box.MinPrice && currentPrice < bet.Box.MaxPrice) ||       // currentPrice> minPrice and currentPrice<maxPrice
                (previousPrice > bet.Box.MaxPrice && currentPrice < bet.Box.MinPrice) ||     // OR previousPrice > maxPrice and currentPrice < minPrice
                (previousPrice < bet.Box.MinPrice && currentPrice > bet.Box.MaxPrice))      // OR previousPrice < minPrice and currentPrice > maxPrice
                return true;
            else
                return false;
        }
        private bool CheckWinOnstarted(GameBet bet, double dCurrentPrice)
        {
            decimal currentPrice = Convert.ToDecimal(dCurrentPrice);
            
            double currentDelta = (double)currentPrice - dCurrentPrice;            

            if (currentDelta > 0.000001 || currentDelta < -0.000001)
                appLog.WriteWarningAsync("GameManager", "CheckWinOnstarted", "", $"Double to Decimal conversion Fail! CurrDelta={currentDelta} double:{dCurrentPrice} decimal:{currentPrice}");
            


            if (currentPrice > bet.Box.MinPrice && currentPrice < bet.Box.MaxPrice)
                return true;
            else
                return false;
        }

        /// <summary>
        /// Performs a check to validate bet WIN
        /// </summary>
        /// <param name="bet">Bet</param>
        private void ProcessBetCheck(GameBet bet, bool IsFirstCheck)
        {
            // Run Check Asynchronously
            Task.Run(() =>
            {
                if (bet == null || bet.BetStatus == BetStates.Win || bet.BetStatus == BetStates.Lose)
                {
                    // bet already processed;
                    return;
                }

                var assetHist = assetCache[bet.AssetPair];
                bool IsWin = false;
                if (IsFirstCheck)
                    IsWin  = CheckWinOnstarted(bet, assetHist.CurrentPrice.MidPrice());
                else
                    IsWin = CheckWinOngoing(bet, assetHist.CurrentPrice.MidPrice(), assetHist.PreviousPrice.MidPrice());
                               
                if (IsWin)
                {
                    // Process WIN
                    ProcessBetWin(bet);
                }
                else
                {
                    BetResult checkres = new BetResult(bet.Box.Id)
                    {
                        BetAmount = bet.BetAmount,
                        Coefficient = bet.Box.Coefficient,
                        MinPrice = bet.Box.MinPrice,
                        MaxPrice = bet.Box.MaxPrice,
                        TimeToGraph = bet.Box.TimeToGraph,
                        TimeLength = bet.Box.TimeLength,

                        PreviousPrice = assetHist.PreviousPrice,
                        CurrentPrice = assetHist.CurrentPrice,

                        Timestamp = bet.Timestamp,
                        TimeToGraphStamp = bet.TimeToGraphStamp,
                        WinStamp = bet.WinStamp,
                        FinishedStamp = bet.FinishedStamp,
                        BetState = (int)bet.BetStatus,
                        IsWin = IsWin
                    };
                    
                    // Report Not WIN to WAMP
                    bet.User.PublishToWamp(checkres);                    
                }
                
            });
        }
                        
        /// <summary>
        /// Set bet status to WIN, update user balance, publish WIN to WAMP, Save to DB
        /// </summary>
        /// <param name="bet">Bet</param>
        /// <param name="res">WinCheck Result</param>
        private void ProcessBetWin(GameBet bet)
        {   
            // Set bet to win
            bet.BetStatus = BetStates.Win;
            bet.WinStamp = DateTime.UtcNow;

            //Update user balance with prize            
            decimal prize = bet.BetAmount * bet.Box.Coefficient;
            bet.User.SetBalance(bet.User.Balance + prize);

            // Publish WIN to WAMP topic            
            var t = Task.Run(() => {
                BetResult checkres = new BetResult(bet.Box.Id)
                {
                    BetAmount = bet.BetAmount,
                    Coefficient = bet.Box.Coefficient,
                    MinPrice = bet.Box.MinPrice,
                    MaxPrice = bet.Box.MaxPrice,
                    TimeToGraph = bet.Box.TimeToGraph,
                    TimeLength = bet.Box.TimeLength,

                    PreviousPrice = assetCache[bet.AssetPair].PreviousPrice,
                    CurrentPrice = assetCache[bet.AssetPair].CurrentPrice,

                    Timestamp = bet.Timestamp,
                    TimeToGraphStamp = bet.TimeToGraphStamp,
                    WinStamp = bet.WinStamp,
                    FinishedStamp = bet.FinishedStamp,
                    BetState = (int)bet.BetStatus,
                    IsWin = true
                };
                // Publish to WAMP topic
                bet.User.PublishToWamp(checkres);
                
                // Save bet to Database
                database.SaveGameBet(bet);

                // Set User Status
                UserState user = GetUserState(bet.UserId);
                SetUserStatus(user, GameStatus.BetWon, $"Bet WON [{bet.Box.Id}] [{bet.AssetPair}] Bet:{bet.BetAmount} Coef:{bet.Box.Coefficient} Prize:{bet.BetAmount * bet.Box.Coefficient}");
            });
            
        }
        /// <summary>
        /// Set bet status to Lose(if not won),  publish WIN to WAMP, Save to DB
        /// </summary>
        /// <param name="bet">Bet</param>
        private void ProcessBetTimeOut(GameBet bet)
        {
            // Remove bet from cache
            // Remove bet from cache
            lock (BetCacheLock)
            {
                bool res = betCache.Remove(bet);
            }

            // If bet was not won previously
            if (bet.BetStatus != BetStates.Win)
            {                
                // Set bet Status to lose
                bet.BetStatus = BetStates.Lose;

                // publish LOSE to WAMP topic                
                var t = Task.Run(() => {
                    BetResult checkres = new BetResult(bet.Box.Id)
                    {
                        BetAmount = bet.BetAmount,
                        Coefficient = bet.Box.Coefficient,
                        MinPrice = bet.Box.MinPrice,
                        MaxPrice = bet.Box.MaxPrice,
                        TimeToGraph = bet.Box.TimeToGraph,
                        TimeLength = bet.Box.TimeLength,

                        PreviousPrice = assetCache.ContainsKey(bet.AssetPair) ? assetCache[bet.AssetPair].PreviousPrice : new InstrumentPrice(),    // BUG: No Prices on Cache 
                        CurrentPrice = assetCache.ContainsKey(bet.AssetPair) ? assetCache[bet.AssetPair].CurrentPrice: new InstrumentPrice(),       // check if there are any prices on cache

                        Timestamp = bet.Timestamp,
                        TimeToGraphStamp = bet.TimeToGraphStamp,
                        WinStamp = bet.WinStamp,
                        FinishedStamp = bet.FinishedStamp,
                        BetState = (int)bet.BetStatus,
                        IsWin = false
                    };
                    // Publish to WAMP topic
                    bet.User.PublishToWamp(checkres);
                    
                    // Save bet to Database
                    database.SaveGameBet(bet);

                    // Set User Status
                    UserState user = GetUserState(bet.UserId);
                    SetUserStatus(user, GameStatus.BetLost, $"Bet LOST [{bet.Box.Id}] [{bet.AssetPair}] Bet:{bet.BetAmount}");
                });
                
                
            }
        }

        #endregion

        /// <summary>
        /// Disposes GameManager Resources
        /// </summary>
        public void Dispose()
        {
            if (isDisposing)
                return;
            isDisposing = true;

            if (CoeffMonitorTimer != null)
            {
                CoeffMonitorTimer.Change(-1, -1);
                CoeffMonitorTimer.Dispose();
                CoeffMonitorTimer = null;
            }
            
            quoteFeed.MessageReceived -= QuoteFeed_MessageReceived;
            betCache = null;

            foreach (var user in userList)
            {
                user.Dispose();
            }

            userList = null;

        }

        #endregion

        #region Event Handlers
        private async void QuoteFeed_MessageReceived(object sender, InstrumentPrice e)
        {
            //Activate Mutual Exclusion Semaphore
            await quoteReceivedSemaphoreSlim.WaitAsync();
            try
            {

                // Add price to cache
                if (!assetCache.ContainsKey(e.Instrument))
                    assetCache.Add(e.Instrument, new PriceCache());

                // Update price cache
                assetCache[e.Instrument].PreviousPrice = assetCache[e.Instrument].CurrentPrice;
                assetCache[e.Instrument].CurrentPrice = (InstrumentPrice)e.ClonePrice();

                // Get bets for current asset
                // That are not yet with WIN status
                var betCacheSnap = GetRunningBets();

                var assetBets = (from b in betCacheSnap
                                 where b.AssetPair == e.Instrument &&
                                 b.BetStatus != BetStates.Win
                                 select b).ToList();
                if (assetBets.Count == 0)
                    return;

                foreach (var bet in assetBets)
                {
                    ProcessBetCheck(bet, false);
                }
            }
            catch (Exception ex) { ReportError("QuoteFeed_MessageReceived", ex); }
            finally { quoteReceivedSemaphoreSlim.Release(); }

        }

        private void Bet_TimeToGraphReached(object sender, EventArgs e)
        {
            try
            {
                GameBet bet = sender as GameBet;
                if (bet == null)
                    return;


                // Do initial Check            
                if (assetCache.ContainsKey(bet.AssetPair))
                {
                    if (assetCache[bet.AssetPair].CurrentPrice != null &&
                        assetCache[bet.AssetPair].PreviousPrice != null &&
                        assetCache[bet.AssetPair].CurrentPrice.MidPrice() > 0 &&
                        assetCache[bet.AssetPair].PreviousPrice.MidPrice() > 0)
                    {
                        ProcessBetCheck(bet, true);
                    }
                }

                // Add bet to cache
                lock (BetCacheLock)
                {
                    betCache.Add(bet);
                }
            }
            catch (Exception ex) { ReportError("Bet_TimeToGraphReached", ex); }
        }
        private void Bet_TimeLenghFinished(object sender, EventArgs e)
        {
            GameBet sdr = sender as GameBet;
            if (sdr == null)
                return;
            ProcessBetTimeOut(sdr);


        }
                
        #endregion

        #region IGameManager
        public BoxSize[] InitUser(string userId)
        {
            return InitializeUser(userId);
        }      

        
         
        public DateTime PlaceBet(string userId, string assetPair, string box, decimal bet,  out string message)
        {            
            var newBet = PlaceNewBet(userId, assetPair, box, bet, out message);
            if (newBet == null)
                return DateTime.MinValue;
            else
                return newBet.Timestamp;
        }

        public decimal SetUserBalance(string userId, decimal newBalance)
        {
            UserState userState = GetUserState(userId);
            userState.SetBalance(newBalance);
                        
            // Log Balance Change
            SetUserStatus(userState, GameStatus.BalanceChanged, $"New Balance: {newBalance}");

            return newBalance;
        }

        public decimal GetUserBalance(string userId)
        {
            UserState userState = GetUserState(userId);
            return userState.Balance;
        }        
               
        public string RequestUserCoeff(string userId, string pair)
        {

            // Request Coeffcalculator Data            
            string result = GetCoefficients(pair);
            return result;
        }

        public void AddUserLog(string userId, string eventCode, string message)
        {
            // Write log to repository
            Task t = logRepository?.InsertAsync(new LogItem()
            {
                ClientId = userId,
                EventCode = eventCode,
                Message = message
            });
            t.Wait();

            // Set Current User Status User
            UserState userState = GetUserState(userId);
            int ecode = -1;
            int.TryParse(eventCode, out ecode);
            SetUserStatus(userState, (GameStatus)ecode, message);
        }
        
        #endregion
                
        #region Nested Class
        private class PriceCache
        {
            public InstrumentPrice CurrentPrice { get; set; }
            public InstrumentPrice PreviousPrice { get; set; }
        }
        #endregion

       
    }
}
