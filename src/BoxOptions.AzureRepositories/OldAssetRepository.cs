﻿using AzureStorage;
using AzureStorage.Tables;
using BoxOptions.Core;
using BoxOptions.Core.Interfaces;
using BoxOptions.Core.Models;
using Microsoft.WindowsAzure.Storage.Table;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;

namespace BoxOptions.AzureRepositories
{
    public class OldAssetEntity : TableEntity, IAssetItem
    {
        public string AssetPair { get; set; }
        public bool IsBuy { get; set; }
        public double Price { get; set; }
        public DateTime Date { get; set; }

        public static string GetPartitionKey(string assetPair)
        {
            return assetPair;
        }

        public static OldAssetEntity Create(IAssetItem src)
        {
            return new OldAssetEntity
            {
                PartitionKey = GetPartitionKey(src.AssetPair),
                AssetPair = src.AssetPair,
                IsBuy = src.IsBuy,
                Price = src.Price,
                Date = src.Date                
            };
        }

        public static IEnumerable<OldAssetEntity> Create(IEnumerable<IAssetItem> src)
        {
            int ctr=0;
            var res = from s in src
                      select new OldAssetEntity
                      {

                          PartitionKey = GetPartitionKey(s.AssetPair),
                          RowKey = string.Format("{0}.{1}", DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm.ss"), ctr++.ToString("D3")),
                          AssetPair = s.AssetPair,
                          IsBuy = s.IsBuy,
                          Price = s.Price,
                          Date = s.Date
                      };
            return res;
            
        }

        public static AssetItem CreateAssetItem(OldAssetEntity src)
        {
            return new AssetItem
            {
                AssetPair = src.AssetPair,
                Date = src.Date,
                Price = src.Price,
                IsBuy = src.IsBuy,                
                ServerTimestamp = src.Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)
            };
        }
    }

    public class OldAssetRepository : IAssetRepository
    {
        private readonly AzureTableStorage<OldAssetEntity> _storage;

        public OldAssetRepository(AzureTableStorage<OldAssetEntity> storage)
        {
            _storage = storage;
        }

        public async Task<IAssetItem> InsertAsync(IAssetItem olapEntity)
        {
            IAssetItem res = await _storage.InsertAndGenerateRowKeyAsDateTimeAsync(OldAssetEntity.Create(olapEntity), DateTime.UtcNow);
            return res;

        }
        bool inserting = false;
        public async Task InsertManyAsync(IEnumerable<IAssetItem> olapEntities)
        {
            if (inserting)
            {
                Console.WriteLine("{0}>Packet Lost: {1}", DateTime.UtcNow.ToString("HH:mm:ss"), olapEntities.Count());
                return;
            }
            inserting = true;
            
            var total = OldAssetEntity.Create(olapEntities);

            var grouping = from e in total
                           group e by new { e.AssetPair } into cms
                           select new { key = cms.Key, val = cms.ToList() };
                        

            foreach (var item in grouping)
            {

                //Console.WriteLine("{0}>Inserting: {1}", DateTime.UtcNow.ToString("HH:mm:ss.fff"), item.val.Count);                
                await _storage.InsertOrMergeBatchAsync(item.val);
                //Console.WriteLine("{0}>Inserted: {1}", DateTime.UtcNow.ToString("HH:mm:ss.fff"), item.val.Count);
            }

            inserting = false;


        }

        public async Task<IEnumerable<AssetItem>> GetRange(DateTime dateFrom, DateTime dateTo, string assetPair)
        {
            var entities = (await _storage.GetDataAsync(new[] { assetPair }, int.MaxValue,
                    entity => entity.Timestamp >= dateFrom && entity.Timestamp < dateTo))
                .OrderByDescending(item => item.Timestamp);

            return entities.Select(OldAssetEntity.CreateAssetItem);
        }

        
    }
}
