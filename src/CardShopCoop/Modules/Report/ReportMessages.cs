using System.Collections.Generic;
using System;
using CardShopCoop.Modules.Prediction;
using CardShopCoop.Net;

namespace CardShopCoop.Modules.Report
{
    [NetworkMessage]
    public sealed class ReportStateMessage : INetMessage
    {
        public bool Full = true;
        public int Index = -1;
        public bool OpenScreen;
        public bool CloseScreen;
        public int CustomerVisited;
        public int CheckoutCount;
        public int CustomerDisatisfied;
        public int CustomerBoughtItem;
        public int CustomerBoughtCard;
        public int CustomerPlayed;
        public int StoreExpGained;
        public int StoreLevelGained;
        public int ItemAmountSold;
        public int CardAmountSold;
        public float TotalPlayTableTime;
        public float TotalItemEarning;
        public float TotalCardEarning;
        public float TotalPlayTableEarning;
        public float SupplyCost;
        public float UpgradeCost;
        public float EmployeeCost;
        public float RentCost;
        public float BillCost;
        public int CardPackOpened;
        public int SmellyCustomerCleaned;
        public int ManualCheckoutCount;
        public int GemMintCardObtained;
        public int ReviewCount;
        public float ReviewScoreAverage;
        public List<ReportReviewEntry> Reviews = new();
    }

    [NetworkMessage]
    public sealed class ReportDeltaMessage : IPredictedMessage
    {
        public Guid PredictionId
        {
            get; set;
        }
        public byte Kind;
        public bool OpenScreen;
        public bool CloseScreen;
        public int CustomerVisited;
        public int CheckoutCount;
        public int CustomerDisatisfied;
        public int CustomerBoughtItem;
        public int CustomerBoughtCard;
        public int CustomerPlayed;
        public int StoreExpGained;
        public int StoreLevelGained;
        public int ItemAmountSold;
        public int CardAmountSold;
        public float TotalPlayTableTime;
        public float TotalItemEarning;
        public float TotalCardEarning;
        public float TotalPlayTableEarning;
        public float SupplyCost;
        public float UpgradeCost;
        public float EmployeeCost;
        public float RentCost;
        public float BillCost;
        public int CardPackOpened;
        public int SmellyCustomerCleaned;
        public int ManualCheckoutCount;
        public int GemMintCardObtained;
        public int ReviewCount;
        public float ReviewScoreAverage;
        public List<ReportReviewEntry> Reviews = new();
    }

    public sealed class ReportReviewEntry
    {
        public int CustomerReviewType;
        public byte StarLevel;
        public byte TextSOGoodBadLevel;
        public int TextSOIndex;
        public int Day;
        public byte Hour;
        public byte Minute;
        public EItemType ItemType;
        public string CustomerName;
    }
}
