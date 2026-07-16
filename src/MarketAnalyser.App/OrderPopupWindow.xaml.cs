using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using MarketAnalyser.Core.Orders;

namespace MarketAnalyser.App;

public partial class OrderPopupWindow : Window
{
    private readonly ObservableCollection<OrderRow> openOrders = [];
    private readonly ObservableCollection<OrderRow> closedOrders = [];
    private readonly ObservableCollection<PositionRow> positions = [];
    private readonly ObservableCollection<HoldingRow> holdings = [];
    private readonly string symbol;
    private readonly string strikeLabel;
    private readonly string optionSide;
    private readonly string detailText;
    private readonly decimal liveLtp;
    private readonly int lotSize;
    private readonly IOrderBroker? orderBroker;
    private readonly bool ordersEnabled;
    private string currentMode = "Open";

    public OrderPopupWindow(
        string symbol,
        string strikeLabel,
        string optionSide,
        string detailText,
        decimal liveLtp,
        int lotSize,
        IOrderBroker? orderBroker = null,
        bool ordersEnabled = false)
    {
        this.symbol = symbol;
        this.strikeLabel = strikeLabel;
        this.optionSide = optionSide;
        this.detailText = detailText;
        this.liveLtp = liveLtp;
        this.lotSize = Math.Max(1, lotSize);
        this.orderBroker = orderBroker;
        this.ordersEnabled = ordersEnabled;

        InitializeComponent();
        TitleText.Text = $"{symbol} order ticket";
        SubtitleText.Text = $"{optionSide} · {strikeLabel} · Lot {this.lotSize:N0}";
        StrikeText.Text = strikeLabel;
        LtpText.Text = $"LTP {liveLtp:N2}";
        DetailText.Text = detailText;
        LotSizeText.Text = $"{this.lotSize:N0} lots";
        PriceBox.Text = liveLtp > 0 ? liveLtp.ToString("N2", CultureInfo.InvariantCulture) : "0";

        OrdersGrid.ItemsSource = openOrders;
        ClosedOrdersGrid.ItemsSource = closedOrders;
        PositionsGrid.ItemsSource = positions;
        HoldingsGrid.ItemsSource = holdings;

        SideBox.SelectedIndex = string.Equals(optionSide, "Sell", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        OrderTypeBox.SelectedIndex = 1;
        ProductBox.SelectedIndex = 2;

        _ = RefreshAllAsync();
    }

    public Task LoadOrdersAsync()
    {
        return RefreshAllAsync();
    }

    private async Task RefreshAllAsync()
    {
        try
        {
            await RefreshOrdersAsync();
            await RefreshPositionsAsync();
            await RefreshHoldingsAsync();
        }
        catch (Exception ex)
        {
            ActionStatusText.Text = ex.Message;
        }
    }

    private async Task RefreshOrdersAsync()
    {
        if (!ordersEnabled || orderBroker is null)
        {
            openOrders.Clear();
            closedOrders.Clear();
            openOrders.Add(new OrderRow("Order service disabled.", string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty));
            return;
        }

        var items = await orderBroker.GetOrdersAsync(CancellationToken.None);
        var openFiltered = items.Where(order => order.Status is not OrderStatus.Filled and not OrderStatus.Cancelled and not OrderStatus.Rejected);
        var closedFiltered = items.Where(order => order.Status is OrderStatus.Filled or OrderStatus.Cancelled or OrderStatus.Rejected);

        openOrders.Clear();
        foreach (var item in openFiltered)
        {
            openOrders.Add(ToOrderRow(item));
        }

        closedOrders.Clear();
        foreach (var item in closedFiltered)
        {
            closedOrders.Add(ToOrderRow(item));
        }
    }

    private static OrderRow ToOrderRow(OrderInfo item)
    {
        return new OrderRow(
            item.CreatedAt.ToLocalTime().ToString("HH:mm:ss"),
            item.Status.ToString(),
            item.Side.ToString(),
            item.Symbol,
            item.Quantity.ToString(CultureInfo.InvariantCulture),
            item.AveragePrice.ToString("N2", CultureInfo.InvariantCulture),
            item.RejectionReason is null ? string.Empty : item.RejectionReason);
    }

    private async Task RefreshPositionsAsync()
    {
        if (!ordersEnabled || orderBroker is null)
        {
            positions.Clear();
            positions.Add(new PositionRow("Order service disabled.", string.Empty, string.Empty, string.Empty, string.Empty));
            return;
        }

        var items = await orderBroker.GetPositionsAsync(CancellationToken.None);
        positions.Clear();
        foreach (var item in items)
        {
            positions.Add(new PositionRow(
                item.Symbol,
                item.Quantity.ToString(CultureInfo.InvariantCulture),
                item.AveragePrice.ToString("N2", CultureInfo.InvariantCulture),
                item.LastPrice.ToString("N2", CultureInfo.InvariantCulture),
                item.Pnl.ToString("N2", CultureInfo.InvariantCulture)));
        }
    }

    private async Task RefreshHoldingsAsync()
    {
        if (!ordersEnabled || orderBroker is null)
        {
            holdings.Clear();
            holdings.Add(new HoldingRow("Order service disabled.", string.Empty, string.Empty, string.Empty, string.Empty));
            return;
        }

        var items = await orderBroker.GetHoldingsAsync(CancellationToken.None);
        holdings.Clear();
        foreach (var item in items)
        {
            var pnl = (item.LastTradedPrice - item.AvgCostPrice) * item.TotalQty;
            holdings.Add(new HoldingRow(
                item.TradingSymbol,
                item.TotalQty.ToString(CultureInfo.InvariantCulture),
                item.AvgCostPrice.ToString("N2", CultureInfo.InvariantCulture),
                item.LastTradedPrice.ToString("N2", CultureInfo.InvariantCulture),
                pnl.ToString("N2", CultureInfo.InvariantCulture)));
        }
    }

    private async Task SubmitOrderAsync()
    {
        if (orderBroker is null)
        {
            ActionStatusText.Text = "Order broker is unavailable.";
            return;
        }

        if (!int.TryParse(QuantityBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var lots) || lots <= 0)
        {
            ActionStatusText.Text = "Enter a valid lot count.";
            return;
        }

        var side = SideBox.SelectedIndex == 1 ? OrderSide.Sell : OrderSide.Buy;
        var product = ProductBox.SelectedIndex switch
        {
            0 => OrderProduct.Intraday,
            1 => OrderProduct.Delivery,
            _ => OrderProduct.Margin
        };
        var orderType = OrderTypeBox.SelectedIndex switch
        {
            0 => OrderType.Market,
            1 => OrderType.Limit,
            2 => OrderType.StopMarket,
            3 => OrderType.StopLimit,
            _ => OrderType.Limit
        };
        var price = decimal.TryParse(PriceBox.Text, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsedPrice) ? parsedPrice : liveLtp;
        var trigger = decimal.TryParse(TriggerBox.Text, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsedTrigger) ? parsedTrigger : 0m;
        var quantity = lots * lotSize;

        var request = new OrderRequest(
            symbol,
            "NSE_FNO",
            $"{symbol}-{optionSide}-{strikeLabel}",
            side,
            quantity,
            product,
            orderType,
            orderType == OrderType.Market ? null : price,
            orderType is OrderType.StopLimit or OrderType.StopMarket ? trigger : null,
            Guid.NewGuid().ToString("N"));

        try
        {
            ActionStatusText.Text = "Submitting order...";
            var response = await orderBroker.PlaceOrderAsync(request, CancellationToken.None);
            ActionStatusText.Text = $"{response.Status}: {response.Message}";
            currentMode = "Open";
            ModeText.Text = "Open orders";
            await RefreshOrdersAsync();
        }
        catch (Exception ex)
        {
            ActionStatusText.Text = ex.Message;
        }
    }

    private async Task ExitSelectedPositionAsync()
    {
        if (orderBroker is null || PositionsGrid.SelectedItem is not PositionRow selected)
        {
            ActionStatusText.Text = "Select a position to exit.";
            return;
        }

        try
        {
            ActionStatusText.Text = $"Exiting {selected.Symbol}...";
            var response = await orderBroker.ExitAllPositionsAsync(CancellationToken.None);
            ActionStatusText.Text = response.Message;
            await RefreshPositionsAsync();
            await RefreshOrdersAsync();
        }
        catch (Exception ex)
        {
            ActionStatusText.Text = ex.Message;
        }
    }

    private async Task ExitAllAsync()
    {
        if (orderBroker is null)
        {
            ActionStatusText.Text = "Order broker is unavailable.";
            return;
        }

        try
        {
            ActionStatusText.Text = "Exiting all positions...";
            var response = await orderBroker.ExitAllPositionsAsync(CancellationToken.None);
            ActionStatusText.Text = response.Message;
            await RefreshPositionsAsync();
            await RefreshOrdersAsync();
        }
        catch (Exception ex)
        {
            ActionStatusText.Text = ex.Message;
        }
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void OpenButton_Click(object sender, RoutedEventArgs e)
    {
        currentMode = "Open";
        ModeText.Text = "Open orders";
        _ = RefreshOrdersAsync();
    }

    private void ClosedButton_Click(object sender, RoutedEventArgs e)
    {
        currentMode = "Closed";
        ModeText.Text = "Closed orders";
        _ = RefreshOrdersAsync();
    }

    private void HoldingsButton_Click(object sender, RoutedEventArgs e)
    {
        _ = RefreshHoldingsAsync();
    }

    private void PositionsButton_Click(object sender, RoutedEventArgs e)
    {
        _ = RefreshPositionsAsync();
    }

    private void ExitAllButton_Click(object sender, RoutedEventArgs e)
    {
        _ = ExitAllAsync();
    }

    private void ExitSelectedPosition_Click(object sender, RoutedEventArgs e)
    {
        _ = ExitSelectedPositionAsync();
    }

    private void SubmitButton_Click(object sender, RoutedEventArgs e)
    {
        _ = SubmitOrderAsync();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
        }
    }

    public sealed record OrderRow(string Time, string Status, string Side, string Symbol, string Quantity, string AveragePrice, string Pnl);

    public sealed record PositionRow(string Symbol, string Quantity, string AveragePrice, string LastPrice, string Pnl);

    public sealed record HoldingRow(string TradingSymbol, string TotalQty, string AvgCostPrice, string LastTradedPrice, string Pnl);
}
