using OpenQA.Selenium;
using OpenQA.Selenium.Chrome;
using OpenQA.Selenium.Support.UI;
using System.Text.RegularExpressions;
using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using SeleniumExtras.WaitHelpers;

class Program
{
    static IWebDriver? driver; // Global driver instance

    static Task HandlePollingErrorAsync(ITelegramBotClient botClient, Exception exception, CancellationToken cancellationToken)
    {
        Console.WriteLine("❌ Error: " + exception.Message); // Catch and log errors during polling and notify when there is an error
        return Task.CompletedTask;
    }

    public static void PressEscapeKey(IWebDriver driver) // Method to press the Escape key 
    {
        new OpenQA.Selenium.Interactions.Actions(driver)
            .SendKeys(OpenQA.Selenium.Keys.Escape)
            .Perform();
    }

    static async Task Main() // Open the website and notify when ready
    {
        var botClient = new TelegramBotClient("7309863025:AAFlHKRAQ7wZcagBjvP5NulRT5w54PRf0Vo"); // Replace with your token
        var cts = new CancellationTokenSource();
        var receiverOptions = new ReceiverOptions { AllowedUpdates = Array.Empty<UpdateType>() };

        botClient.StartReceiving(
            updateHandler: HandleUpdateAsync,
            pollingErrorHandler: HandlePollingErrorAsync,
            receiverOptions: receiverOptions,
            cancellationToken: cts.Token
        );

        var me = await botClient.GetMeAsync();
        Console.WriteLine("Pocket Option Bot is up and running.");

        ChromeOptions options = new ChromeOptions();
        driver = new ChromeDriver(options);
        driver.Navigate().GoToUrl("https://pocketoption.com/en/login/");
        Console.WriteLine("Pocket Option opened in Chrome.");
        Console.WriteLine("Waiting for login...");

        WebDriverWait wait = new WebDriverWait(driver, TimeSpan.FromMinutes(3));
        try
        {
            wait.Until(ExpectedConditions.ElementIsVisible(By.CssSelector("div.pair")));
            Console.WriteLine("✅ Pocket Option is ready to accept trades!");
        }
        catch (WebDriverTimeoutException)
        {
            Console.WriteLine("❌ Login timeout — trading page did not load in time.");
            return;
        }

        Console.WriteLine("Ready to receive trades from Telegram.");
        Console.ReadLine(); // Keep app alive
    }

    static async Task HandleUpdateAsync(ITelegramBotClient botClient, Update update, CancellationToken cancellationToken)
    {
        if (update.Message is not { } message || message.Text is not { } text)
            return;

        Console.WriteLine($"📩 Received message: {text}");

        string? pair = null;
        string? action = null;
        string? time = null;
        bool isOTC = false;
        var lower = text.ToLower();

        if (text.Contains("Trade Signal!", StringComparison.OrdinalIgnoreCase))
        {
            var pairMatch = Regex.Match(text, @"Currency Pair:\s*([A-Z]{3}/[A-Z]{3})( OTC)?", RegexOptions.IgnoreCase);
            if (pairMatch.Success)
            {
                pair = pairMatch.Groups[1].Value.ToUpper();
                isOTC = !string.IsNullOrEmpty(pairMatch.Groups[2].Value);
            }

            var actionMatch = Regex.Match(text, @"Trade Signal:\s*(OPEN\s+BUY|OPEN\s+SELL)", RegexOptions.IgnoreCase);
            if (actionMatch.Success)
            {
                action = actionMatch.Groups[1].Value.ToUpper().Replace("OPEN ", "");
            }

            var timeMatch = Regex.Match(text, @"Timeframe:\s*(\d+)\s*minute", RegexOptions.IgnoreCase);
            if (timeMatch.Success)
            {
                var minutes = timeMatch.Groups[1].Value;
                time = $"{minutes}m";
            }
        }
        else
        {
            isOTC = lower.Contains("otc");
            if (lower.Contains("otc"))
            {
                isOTC = true;
                lower = lower.Replace("otc", "").Trim();
            }

            var pairMatch = Regex.Match(lower, @"([a-z]{3})/([a-z]{3})");
            if (pairMatch.Success)
                pair = pairMatch.Value.ToUpper();

            var timeParts = Regex.Matches(lower, @"(\d+)\s*[hms]");
            if (timeParts.Count > 0)
                time = string.Join(" ", timeParts.Select(m => m.Value));

            if (lower.Contains("open buy") || lower.Contains("buy")) action = "BUY";
            else if (lower.Contains("open sell") || lower.Contains("sell")) action = "SELL";
        }

        string response;
        if (pair != null && action != null && time != null)
        {
            response = $"🪙 Pair: {pair}{(isOTC ? " OTC" : "")}\n⏱️ Time: {time}\n📈 Action: {action}";

            if (SelectCurrencyPair(pair, isOTC))
            {
                SetTradeTime(time);
                ExecuteTrade(action);
            }
            else
            {
                await botClient.SendTextMessageAsync(
                    chatId: message.Chat.Id,
                    text: $"❌ Trade cancelled: {pair}{(isOTC ? " OTC" : "")} is unavailable (N/A).",
                    cancellationToken: cancellationToken
                );
            }
        }
        else
        {
            response = $"⚠️ Wrong trade info.\n🪙 Pair: {pair}\n📈 Action: {action}\n⏱️ Time: {time}";
        }

        await botClient.SendTextMessageAsync(
            chatId: message.Chat.Id,
            text: response,
            cancellationToken: cancellationToken
        );
    }


    static bool SelectCurrencyPair(string pair, bool isOTC) // Selects the currency pair and check if it's available & OTC
    {
        try
        {
            // Step 1: Click the pair button (top left)
            var pairButton = driver.FindElement(By.CssSelector("div.pair"));
            pairButton.Click();
            Thread.Sleep(400); // Allow animation to avoid miss click 

            // Step 2: Search for the pair
            var searchInput = driver.FindElement(By.CssSelector("input.search__field"));
            searchInput.Click();
            searchInput.Clear();
            searchInput.SendKeys(pair);
            Thread.Sleep(500); // Wait for results to show

            // Step 3: Find all results
            var results = driver.FindElements(By.XPath("//li[contains(@class,'alist__item')]"));

            foreach (var result in results)
            {
                var label = result.FindElement(By.CssSelector("span.alist__label")).Text.ToUpper().Trim();
                string expectedLabel = isOTC ? pair + " OTC" : pair;

                if (label == expectedLabel)
                {
                    // Step 4: Check payout N/A 
                    var payoutElement = result.FindElement(By.CssSelector("span.alist__payout span"));
                    string payoutText = payoutElement.Text.Trim();

                    if (payoutText == "N/A")
                    {
                        Console.WriteLine($"⚠️ Pair '{pair}' is N/A — clicked but trade canceled.");
                        PressEscapeKey(driver);

                        return false;
                    }

                    result.Click(); // ✅ Pair is valid → click
                    Thread.Sleep(200);
                    PressEscapeKey(driver);

                    return true;
                }
            }
            Console.WriteLine("❌ Could not find matching pair.");
            PressEscapeKey(driver);

            return false;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Error selecting pair: {ex.Message}"); // if element selector fails
            return false;
        }
    }

    static void SetTradeTime(string time)
    {
        try
        { 
            // Step 1: Click to open the popup
            var timeDisplay = driver.FindElement(By.CssSelector(".control__value"));
            timeDisplay.Click();
            Thread.Sleep(500); // Wait for popup to appear

            // Step 2: Parse values from string like "1h 5m 0s"
            int hour = 0, minute = 0, second = 0;

            var hourMatch = Regex.Match(time, @"(\d+)\s*h");
            var minuteMatch = Regex.Match(time, @"(\d+)\s*m");
            var secondMatch = Regex.Match(time, @"(\d+)\s*s");

            if (hourMatch.Success)
                hour = int.Parse(hourMatch.Groups[1].Value);

            if (minuteMatch.Success)
                minute = int.Parse(minuteMatch.Groups[1].Value);

            if (secondMatch.Success)
                second = int.Parse(secondMatch.Groups[1].Value);

            // Step 3: Get input fields: 0 = hour, 1 = minute, 2 = second
            var inputs = driver.FindElements(By.CssSelector(".input-field-wrapper input"));

            if (inputs.Count < 3)
            {
                Console.WriteLine("❌ Could not find all 3 time input fields.");
                return;
            }

            // Paste simulation using JS instead of SendKeys
            void PasteInput(IWebElement input, int value)
            {
                input.Click();
                Thread.Sleep(100);
                ((IJavaScriptExecutor)driver).ExecuteScript($"arguments[0].value = '{value}';", input);
            }

            PasteInput(inputs[0], hour);
            PasteInput(inputs[1], minute);
            PasteInput(inputs[2], second);
            Thread.Sleep(200);

            // Step 4: Close the popup
            timeDisplay.Click();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Failed to set trade time: {ex.Message}");
        }
    }

    static void ExecuteTrade(string action)
    {
        try
        { 
        
            if (action.ToUpper() == "BUY")
            {
                var buyButton = driver.FindElement(By.CssSelector("a.btn-call")); // Look for the Buy button
                buyButton.Click();
            }
            else if (action.ToUpper() == "SELL")
            {
                var sellButton = driver.FindElement(By.CssSelector("a.btn-put")); // Look for the Sell button
            sellButton.Click();
            }
            else
            {
                Console.WriteLine("⚠️ Invalid trade action.");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Failed to execute trade: {ex.Message}");
        }
    }
}