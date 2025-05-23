using System.Diagnostics.CodeAnalysis;
using Content.Server._NF.Bank; // Frontier
using Content.Server.Cargo.Components;
using Content.Server.Labels.Components;
using Content.Shared._NF.Bank.Components; // Frontier
using Content.Server.Station.Components;
using Content.Shared.Cargo;
using Content.Shared.Cargo.BUI;
using Content.Shared.Cargo.Components;
using Content.Shared.Cargo.Events;
using Content.Shared.Cargo.Prototypes;
using Content.Shared.Database;
using Content.Shared.Emag.Systems;
using Content.Shared.IdentityManagement;
using Content.Shared.Interaction;
using Content.Shared.Paper;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;
using Robust.Shared.Utility;
using System.Linq;
using Content.Shared._NF.Bank.BUI; // Frontier

namespace Content.Server.Cargo.Systems
{
    public sealed partial class CargoSystem
    {
        [Dependency] private readonly SharedTransformSystem _transformSystem = default!;
        [Dependency] private readonly EmagSystem _emag = default!;

        /// <summary>
        /// How much time to wait (in seconds) before increasing bank accounts balance.
        /// </summary>
        private const int Delay = 10;

        /// <summary>
        /// Keeps track of how much time has elapsed since last balance increase.
        /// </summary>
        private float _timer;

        [Dependency] private readonly BankSystem _bankSystem = default!;

        private void InitializeConsole()
        {
            SubscribeLocalEvent<CargoOrderConsoleComponent, CargoConsoleAddOrderMessage>(OnAddOrderMessage);
            SubscribeLocalEvent<CargoOrderConsoleComponent, CargoConsoleRemoveOrderMessage>(OnRemoveOrderMessage);
            SubscribeLocalEvent<CargoOrderConsoleComponent, CargoConsoleApproveOrderMessage>(OnApproveOrderMessage);
            SubscribeLocalEvent<CargoOrderConsoleComponent, BoundUIOpenedEvent>(OnOrderUIOpened);
            SubscribeLocalEvent<CargoOrderConsoleComponent, ComponentInit>(OnInit);
            //SubscribeLocalEvent<CargoOrderConsoleComponent, InteractUsingEvent>(OnInteractUsing); //Frontier Disabled
            SubscribeLocalEvent<CargoOrderConsoleComponent, BankBalanceUpdatedEvent>(OnOrderBalanceUpdated);
            SubscribeLocalEvent<CargoOrderConsoleComponent, GotEmaggedEvent>(OnEmagged);
            SubscribeLocalEvent<CargoOrderConsoleComponent, GotUnEmaggedEvent>(OnUnemagged); // Frontier
            Reset();
        }

        // Frontier: disabled
        /*
        private void OnInteractUsing(EntityUid uid, CargoOrderConsoleComponent component, ref InteractUsingEvent args)
        {
            if (!HasComp<CashComponent>(args.Used))
                return;

            var price = _pricing.GetPrice(args.Used);

            if (price == 0)
                return;

            var stationUid = _station.GetOwningStation(args.Used);

            if (!TryComp(stationUid, out StationBankAccountComponent? bank))
                return;

            _audio.PlayPvs(component.ConfirmSound, uid);
            UpdateBankAccount(stationUid.Value, bank, (int) price);
            QueueDel(args.Used);
            args.Handled = true;
        }
        */
        // End Frontier

        private void OnInit(EntityUid uid, CargoOrderConsoleComponent orderConsole, ComponentInit args)
        {
            var station = _station.GetOwningStation(uid);
            UpdateOrderState(uid, orderConsole, station);
        }

        private void Reset()
        {
            _timer = 0;
        }

        private void OnEmagged(Entity<CargoOrderConsoleComponent> ent, ref GotEmaggedEvent args)
        {
            if (!_emag.CompareFlag(args.Type, EmagType.Interaction))
                return;

            if (_emag.CheckFlag(ent, EmagType.Interaction))
                return;

            args.Handled = true;
        }

        // Frontier: demag
        private void OnUnemagged(Entity<CargoOrderConsoleComponent> ent, ref GotUnEmaggedEvent args)
        {
            if (!_emag.CompareFlag(args.Type, EmagType.Interaction))
                return;

            if (!_emag.CheckFlag(ent, EmagType.Interaction))
                return;

            args.Handled = true;
        }
        // End Frontier: demag

        private void UpdateConsole(float frameTime)
        {
            _timer += frameTime;

            // TODO: Doesn't work with serialization and shouldn't just be updating every delay
            // client can just interp this just fine on its own.
            while (_timer > Delay)
            {
                _timer -= Delay;

                var stationQuery = EntityQueryEnumerator<StationBankAccountComponent>();
                while (stationQuery.MoveNext(out var uid, out var bank))
                {
                    var balanceToAdd = bank.IncreasePerSecond * Delay;
                    UpdateBankAccount(uid, bank, balanceToAdd);
                }

                var query = EntityQueryEnumerator<CargoOrderConsoleComponent>();
                while (query.MoveNext(out var uid, out var comp))
                {
                    if (!_uiSystem.IsUiOpen(uid, CargoConsoleUiKey.Orders)) continue;

                    var station = _station.GetOwningStation(uid);
                    UpdateOrderState(uid, comp, station);
                }
            }
        }

        #region Interface

        private void OnApproveOrderMessage(EntityUid uid, CargoOrderConsoleComponent component, CargoConsoleApproveOrderMessage args)
        {
            if (args.Actor is not { Valid: true } player)
                return;

            if (!_accessReaderSystem.IsAllowed(player, uid))
            {
                ConsolePopup(args.Actor, Loc.GetString("cargo-console-order-not-allowed"));
                PlayDenySound(uid, component);
                return;
            }

            var station = _station.GetOwningStation(uid);

            // Frontier: orders require a bank account.
            if (!TryComp<BankAccountComponent>(player, out var bankAccount))
            {
                ConsolePopup(args.Actor, Loc.GetString("cargo-console-nf-no-bank-account"));
                PlayDenySound(uid, component);
                return;
            }

            // No station to deduct from.
            // Frontier: no checks for StationData/StationBankAccount.
            if (station == null
                || !TryGetOrderDatabase(uid, out var dbUid, out var orderDatabase, component))
            {
                ConsolePopup(args.Actor, Loc.GetString("cargo-console-station-not-found"));
                PlayDenySound(uid, component);
                return;
            }

            // Find our order again. It might have been dispatched or approved already
            var order = orderDatabase.Orders.Find(order => args.OrderId == order.OrderId && !order.Approved);
            if (order == null)
            {
                return;
            }

            // Invalid order
            if (!_protoMan.HasIndex<EntityPrototype>(order.ProductId))
            {
                ConsolePopup(args.Actor, Loc.GetString("cargo-console-invalid-product"));
                PlayDenySound(uid, component);
                return;
            }

            var amount = GetOutstandingOrderCount(orderDatabase);
            var capacity = orderDatabase.Capacity;

            // Too many orders, avoid them getting spammed in the UI.
            if (amount >= capacity)
            {
                ConsolePopup(args.Actor, Loc.GetString("cargo-console-too-many"));
                PlayDenySound(uid, component);
                return;
            }

            // Cap orders so someone can't spam thousands.
            var cappedAmount = Math.Min(capacity - amount, order.OrderQuantity);

            if (cappedAmount != order.OrderQuantity)
            {
                order.OrderQuantity = cappedAmount;
                ConsolePopup(args.Actor, Loc.GetString("cargo-console-snip-snip"));
                PlayDenySound(uid, component);
            }

            var cost = order.Price * order.OrderQuantity;

            // Not enough balance
            if (cost > bankAccount.Balance)
            {
                ConsolePopup(args.Actor, Loc.GetString("cargo-console-insufficient-funds", ("cost", cost)));
                PlayDenySound(uid, component);
                return;
            }

            // Frontier: no cargo fulfillment check
            //var ev = new FulfillCargoOrderEvent((station.Value, stationData), order, (uid, component));
            //RaiseLocalEvent(ref ev); // Frontier
            //ev.FulfillmentEntity ??= station.Value; // Frontier

            // if (!ev.Handled)
            // {
            //     ev.FulfillmentEntity = TryFulfillOrder((station.Value, stationData), order, orderDatabase);

            //     if (ev.FulfillmentEntity == null)
            //     {
            //         ConsolePopup(args.Actor, Loc.GetString("cargo-console-unfulfilled"));
            //         PlayDenySound(uid, component);
            //         return;
            //     }
            // }
            // End Frontier

            order.Approved = true;
            _audio.PlayPvs(component.ConfirmSound, uid);

            if (!_emag.CheckFlag(uid, EmagType.Interaction))
            {
                var tryGetIdentityShortInfoEvent = new TryGetIdentityShortInfoEvent(uid, player);
                RaiseLocalEvent(tryGetIdentityShortInfoEvent);
                order.SetApproverData(tryGetIdentityShortInfoEvent.Title);

                // Frontier: silence cargo station
                // var message = Loc.GetString("cargo-console-unlock-approved-order-broadcast",
                //     ("productName", Loc.GetString(order.ProductName)),
                //     ("orderAmount", order.OrderQuantity),
                //     ("approver", order.Approver ?? string.Empty),
                //     ("cost", cost));
                // _radio.SendRadioMessage(uid, message, component.AnnouncementChannel, uid, escapeMarkup: false);
                // End Frontier: silence cargo station
            }

            ConsolePopup(args.Actor, Loc.GetString("cargo-console-trade-station", ("destination", MetaData(uid).EntityName))); // ev.FulfillmentEntity.Value<uid

            // Log order approval
            _adminLogger.Add(LogType.Action, LogImpact.Low,
                $"{ToPrettyString(player):user} approved order [orderId:{order.OrderId}, quantity:{order.OrderQuantity}, product:{order.ProductId}, requester:{order.Requester}, reason:{order.Reason}] with balance at {bankAccount.Balance}");

            // orderDatabase.Orders.Remove(order); // Frontier

            // Frontier: account balances, taxing vendor purchases
            foreach (var (account, taxCoeff) in component.TaxAccounts)
            {
                if (!float.IsFinite(taxCoeff) || taxCoeff <= 0.0f)
                    continue;
                var tax = (int)Math.Floor(cost * taxCoeff);
                _bankSystem.TrySectorDeposit(account, tax, LedgerEntryType.CargoTax);
            }
            _bankSystem.TryBankWithdraw(player, cost);
            // End Frontier

            UpdateOrders(station.Value);
        }

        // Frontier - consoleUid is required to find cargo pads
        // Only consoleUid is added thats the frontier change
        private EntityUid? TryFulfillOrder(EntityUid consoleUid, StationDataComponent stationData, CargoOrderData order, StationCargoOrderDatabaseComponent orderDatabase)
        {
            // No slots at the trade station
            _listEnts.Clear();
            GetTradeStations(stationData, ref _listEnts);
            EntityUid? tradeDestination = null;

            // Try to fulfill from any station where possible, if the pad is not occupied.
            foreach (var trade in _listEnts)
            {
                var tradePads = GetCargoPallets(consoleUid, trade, BuySellType.Buy);
                _random.Shuffle(tradePads);

                var freePads = GetFreeCargoPallets(trade, tradePads);
                if (freePads.Count >= order.OrderQuantity) //check if the station has enough free pallets
                {
                    foreach (var pad in freePads)
                    {
                        var coordinates = new EntityCoordinates(trade, pad.Transform.LocalPosition);

                        if (FulfillOrder(order, coordinates, orderDatabase.PrinterOutput))
                        {
                            tradeDestination = trade;
                            order.NumDispatched++;
                            if (order.OrderQuantity <= order.NumDispatched) //Spawn a crate on free pellets until the order is fulfilled.
                                break;
                        }
                    }
                }

                if (tradeDestination != null)
                    break;
            }

            return tradeDestination;
        }

        private void GetTradeStations(StationDataComponent data, ref List<EntityUid> ents)
        {
            foreach (var gridUid in data.Grids)
            {
                if (!_tradeQuery.HasComponent(gridUid))
                    continue;

                ents.Add(gridUid);
            }
        }

        private void OnRemoveOrderMessage(EntityUid uid, CargoOrderConsoleComponent component, CargoConsoleRemoveOrderMessage args)
        {
            if (!TryGetOrderDatabase(uid, out var dbUid, out var orderDatabase, component))
                return;

            RemoveOrder(dbUid!.Value, args.OrderId, orderDatabase);
        }

        private void OnAddOrderMessage(EntityUid uid, CargoOrderConsoleComponent component, CargoConsoleAddOrderMessage args)
        {
            if (args.Actor is not { Valid: true } player)
                return;

            if (args.Amount <= 0)
                return;

            var bank = GetBankAccount(uid, component);

            if (!HasComp<BankAccountComponent>(player) && bank == null) return;


            if (!TryGetOrderDatabase(uid, out var dbUid, out var orderDatabase, component))
                return;

            if (!_protoMan.TryIndex<CargoProductPrototype>(args.CargoProductId, out var product))
            {
                Log.Error($"Tried to add invalid cargo product {args.CargoProductId} as order!");
                return;
            }

            if (!component.AllowedGroups.Contains(product.Group))
                return;

            var data = GetOrderData(EntityManager.GetNetEntity(uid), args, product, GenerateOrderId(orderDatabase));

            if (!TryAddOrder(orderDatabase.Owner, data, orderDatabase))
            {
                PlayDenySound(uid, component);
                return;
            }

            // Log order addition
            _adminLogger.Add(LogType.Action, LogImpact.Low,
                $"{ToPrettyString(player):user} added order [orderId:{data.OrderId}, quantity:{data.OrderQuantity}, product:{data.ProductId}, requester:{data.Requester}, reason:{data.Reason}]");

        }

        private void OnOrderUIOpened(EntityUid uid, CargoOrderConsoleComponent component, BoundUIOpenedEvent args)
        {
            var station = _station.GetOwningStation(uid);
            UpdateOrderState(uid, component, station);
        }

        #endregion


        private void OnOrderBalanceUpdated(Entity<CargoOrderConsoleComponent> ent, ref BankBalanceUpdatedEvent args)
        {
            if (!_uiSystem.IsUiOpen(ent.Owner, CargoConsoleUiKey.Orders))
                return;

            UpdateOrderState(ent, ent.Comp, args.Station); // Frontier: add ent.Comp
        }

        // Frontier: custom UpdateOrderState function
        private void UpdateOrderState(EntityUid uid, CargoOrderConsoleComponent component, EntityUid? station)
        {
            var uiUsers = _uiSystem.GetActors((uid, null), CargoConsoleUiKey.Orders);
            foreach (var user in uiUsers)
            {
                var balance = 0;

                if (Transform(user).GridUid is EntityUid stationGrid &&
                    TryComp<BankAccountComponent>(user, out var playerBank))
                {
                    station = stationGrid;
                    balance = playerBank.Balance;
                }
                else if (TryComp<StationBankAccountComponent>(station, out var stationBank))
                {
                    balance = stationBank.Balance;
                }

                if (station == null || !TryGetOrderDatabase(station.Value, out var _, out var orderDatabase, component))
                    return;

                // Frontier - we only want to see orders made on the same computer, so filter them out
                var filteredOrders = orderDatabase.Orders
                    .Where(order => order.Computer == EntityManager.GetNetEntity(uid)).ToList();

                var state = new CargoConsoleInterfaceState(
                    MetaData(user).EntityName,
                    GetOutstandingOrderCount(orderDatabase),
                    orderDatabase.Capacity,
                    balance,
                    filteredOrders);

                _uiSystem.SetUiState(uid, CargoConsoleUiKey.Orders, state);
            }
        }
        // End Frontier

        private void ConsolePopup(EntityUid actor, string text)
        {
            _popup.PopupCursor(text, actor);
        }

        private void PlayDenySound(EntityUid uid, CargoOrderConsoleComponent component)
        {
            _audio.PlayPvs(_audio.ResolveSound(component.ErrorSound), uid);
        }

        private static CargoOrderData GetOrderData(NetEntity consoleUid, CargoConsoleAddOrderMessage args, CargoProductPrototype cargoProduct, int id)
        {
            return new CargoOrderData(id, cargoProduct.Product, cargoProduct.Name, cargoProduct.Cost, args.Amount, args.Requester, args.Reason, consoleUid);
        }

        public static int GetOutstandingOrderCount(StationCargoOrderDatabaseComponent component)
        {
            var amount = 0;

            foreach (var order in component.Orders)
            {
                if (!order.Approved)
                    continue;
                amount += order.OrderQuantity - order.NumDispatched;
            }

            return amount;
        }

        /// <summary>
        /// Updates all of the cargo-related consoles for a particular station.
        /// This should be called whenever orders change.
        /// </summary>
        private void UpdateOrders(EntityUid dbUid)
        {
            // Order added so all consoles need updating.
            var orderQuery = AllEntityQuery<CargoOrderConsoleComponent>();

            while (orderQuery.MoveNext(out var uid, out var comp))
            {
                var station = _station.GetOwningStation(uid);
                if (station != dbUid)
                    continue;

                UpdateOrderState(uid, comp, station);
            }

            var consoleQuery = AllEntityQuery<CargoShuttleConsoleComponent>();
            while (consoleQuery.MoveNext(out var uid, out var _))
            {
                var station = _station.GetOwningStation(uid);
                if (station != dbUid)
                    continue;

                UpdateShuttleState(uid, station);
            }
        }

        public bool AddAndApproveOrder(
            EntityUid dbUid,
            string spawnId,
            string name,
            int cost,
            int qty,
            string sender,
            string description,
            string dest,
            StationCargoOrderDatabaseComponent component,
            StationDataComponent stationData
        )
        {
            DebugTools.Assert(_protoMan.HasIndex<EntityPrototype>(spawnId));
            // Make an order
            var id = GenerateOrderId(component);
            var order = new CargoOrderData(id, spawnId, name, cost, qty, sender, description, null);

            // Approve it now
            order.SetApproverData(dest, sender);
            order.Approved = true;

            // Log order addition
            _adminLogger.Add(LogType.Action, LogImpact.Low,
                $"AddAndApproveOrder {description} added order [orderId:{order.OrderId}, quantity:{order.OrderQuantity}, product:{order.ProductId}, requester:{order.Requester}, reason:{order.Reason}]");

            // Add it to the list
            return TryAddOrder(dbUid, order, component);
        }

        private bool TryAddOrder(EntityUid dbUid, CargoOrderData data, StationCargoOrderDatabaseComponent component)
        {
            component.Orders.Add(data);
            UpdateOrders(dbUid);
            return true;
        }

        private static int GenerateOrderId(StationCargoOrderDatabaseComponent orderDB)
        {
            // We need an arbitrary unique ID to identify orders, since they may
            // want to be cancelled later.
            return ++orderDB.NumOrdersCreated;
        }

        public void RemoveOrder(EntityUid dbUid, int index, StationCargoOrderDatabaseComponent orderDB)
        {
            var sequenceIdx = orderDB.Orders.FindIndex(order => order.OrderId == index);
            if (sequenceIdx != -1)
            {
                orderDB.Orders.RemoveAt(sequenceIdx);
            }
            UpdateOrders(dbUid);
        }

        public void ClearOrders(StationCargoOrderDatabaseComponent component)
        {
            if (component.Orders.Count == 0)
                return;

            component.Orders.Clear();
        }

        private static bool PopFrontOrder(List<NetEntity> consoleUidList, StationCargoOrderDatabaseComponent orderDB, [NotNullWhen(true)] out CargoOrderData? orderOut)
        {
            var orderIdx = orderDB.Orders.FindIndex(order => order.Approved && consoleUidList.Any(consoleUid => consoleUid == order.Computer));
            if (orderIdx == -1)
            {
                orderOut = null;
                return false;
            }

            orderOut = orderDB.Orders[orderIdx];
            orderOut.NumDispatched++;

            if (orderOut.NumDispatched >= orderOut.OrderQuantity)
            {
                // Order is complete. Remove from the queue.
                orderDB.Orders.RemoveAt(orderIdx);
            }
            return true;
        }

        /// <summary>
        /// Tries to fulfill the next outstanding order.
        /// </summary>
        private bool FulfillNextOrder(List<NetEntity> consoleUidList, StationCargoOrderDatabaseComponent orderDB, EntityCoordinates spawn, string? paperProto)
        {
            if (!PopFrontOrder(consoleUidList, orderDB, out var order))
                return false;

            return FulfillOrder(order, spawn, paperProto);
        }

        /// <summary>
        /// Fulfills the specified cargo order and spawns paper attached to it.
        /// </summary>
        private bool FulfillOrder(CargoOrderData order, EntityCoordinates spawn, string? paperProto)
        {
            // Create the item itself
            var item = Spawn(order.ProductId, spawn);

            // Ensure the item doesn't start anchored
            _transformSystem.Unanchor(item, Transform(item));

            // Create a sheet of paper to write the order details on
            var printed = EntityManager.SpawnEntity(paperProto, spawn);
            if (TryComp<PaperComponent>(printed, out var paper))
            {
                // fill in the order data
                var val = Loc.GetString("cargo-console-paper-print-name", ("orderNumber", order.OrderId));
                _metaSystem.SetEntityName(printed, val);

                _paperSystem.SetContent((printed, paper), Loc.GetString(
                        "cargo-console-paper-print-text",
                        ("orderNumber", order.OrderId),
                        ("itemName", MetaData(item).EntityName),
                        ("orderQuantity", order.OrderQuantity),
                        ("requester", order.Requester),
                        ("reason", order.Reason),
                        ("approver", order.Approver ?? string.Empty)));

                // attempt to attach the label to the item
                if (TryComp<PaperLabelComponent>(item, out var label))
                {
                    _slots.TryInsert(item, label.LabelSlot, printed, null);
                }
            }

            return true;

        }

        #region Station

        private StationBankAccountComponent? GetBankAccount(EntityUid uid, CargoOrderConsoleComponent _)
        {
            var station = _station.GetOwningStation(uid);

            TryComp<StationBankAccountComponent>(station, out var bankComponent);
            return bankComponent;
        }

        private bool TryGetOrderDatabase(EntityUid uid, [MaybeNullWhen(false)] out EntityUid? dbUid, [MaybeNullWhen(false)] out StationCargoOrderDatabaseComponent dbComp, CargoOrderConsoleComponent _)
        {
            dbUid = _station.GetOwningStation(uid);
            return TryComp(dbUid, out dbComp);
        }

        #endregion
    }
}
