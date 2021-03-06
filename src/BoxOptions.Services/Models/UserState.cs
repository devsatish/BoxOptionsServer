﻿using BoxOptions.Common.Interfaces;
using BoxOptions.Core.Models;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Subjects;
using WampSharp.V2.Realm;

namespace BoxOptions.Services.Models
{
    public class UserState : IDisposable
    {
        readonly string userId;
        decimal balance;
        int currentState;
        List<UserHistory> statusHistory;
        List<GameBet> openBets;                     // Bet cache
        ISubject<BetResult> subject;                // WAMP Subject

        
        public UserState(string userId)
        {
            this.userId = userId;
            statusHistory = new List<UserHistory>();
            openBets = new List<GameBet>();
            subject = null;
            LastChange = DateTime.UtcNow;
        }
        public UserState(string userId, decimal balance, int currentState)
            : this(userId)
        {
            this.balance = balance;
            this.currentState = currentState;
        }


        /// <summary>
        /// Unique User Id
        /// </summary>
        public string UserId { get => userId; }
        /// <summary>
        /// User Balance
        /// </summary>
        public decimal Balance { get => balance; }
        public int CurrentState { get => currentState; }
        public UserHistory[] StatusHistory => statusHistory.ToArray();
        public DateTime LastChange { get; set; }
        public GameBet[] OpenBets  { get => openBets.ToArray(); }
    

        public void SetBalance(decimal newBalance)
        {
            balance = newBalance;
            LastChange = DateTime.UtcNow;
        }
        internal UserHistory SetStatus(int status, string message)
        {
            UserHistory newEntry = new UserHistory(userId)
            {
                Timestamp = DateTime.UtcNow,
                Status = status,
                Message = message
            };
            statusHistory.Add(newEntry);

            // Keep load history buffer to 20 items
            if (statusHistory.Count > 20)
            {
                statusHistory.RemoveAt(0);
            }

            currentState = status;
            LastChange = DateTime.UtcNow;
            
            return newEntry;
        }

        internal GameBet PlaceBet(Box boxObject, string assetPair, decimal bet, BoxSize boxConfig)
        {
            GameBet retval = new GameBet(this)
            {
                AssetPair = assetPair,
                BetAmount = bet,
                BetStatus = BetStates.Waiting,
                Box = boxObject,
                CurrentParameters = boxConfig,
                Timestamp = DateTime.UtcNow
            };
            openBets.Add(retval);
            // keep bet cache to 1000
            if (openBets.Count > 1000)
                openBets.RemoveAt(0);

            return retval;
        }
        //internal void LoadBets(IEnumerable<GameBet> bets)
        //{
        //    openBets = new List<GameBet>(); ;
        //    openBets.AddRange(bets);
        //}

        internal void StartWAMP(IWampHostedRealm wampRealm, string topicName)
        {
            subject = wampRealm.Services.GetSubject<BetResult>(topicName + "." + userId);
        }
        internal void PublishToWamp(BetResult betResult)
        {
            if (subject == null)
                throw new InvalidOperationException("Wamp Subject not set");

            subject.OnNext(betResult);
        }

        public void Dispose()
        {
            subject = null;

            foreach (var bet in openBets)
            {
                bet.Dispose();
            }
        }

       

        public override string ToString()
        {
            return userId;
        }
    }
}