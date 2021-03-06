﻿using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace BoxOptions.Core
{
    public interface IGameRepository
    {
        Task InsertGameBetAsync(IEnumerable<Interfaces.IGameBetItem> olapEntities);
        Task<IEnumerable<Models.GameBetItem>> GetGameBetsByUser(string userId, DateTime dateFrom, DateTime dateTo);
        Task<IEnumerable<Models.GameBetItem>> GetGameBetsByUser(string userId, DateTime dateFrom, DateTime dateTo, int betState);
    }
}
