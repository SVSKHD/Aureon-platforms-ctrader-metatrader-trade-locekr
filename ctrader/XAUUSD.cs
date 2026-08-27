// ---------------------------------------------------------------------------
//  AureonAxis_A8.cs   —   cTrader cBot
//
//  Direct port of AureonAxis_A8.mq5. Same decisions, same distances, so the
//  two backtests are comparable trade for trade.
//
//  MECHANISM (per session, both directions)
//    anchor   close of the M1 bar at the session anchor minute
//    pivot    first close Disp ABOVE the anchor, UP-MOVES ONLY, between
//             MinElapsed and PivotWindow minutes, not overshooting the
//             anchor by more than MaxPivotDisp
//    confirm  first close Conf either side of the pivot
//                 above -> BUY      below -> SELL
//             the market picks the side; the bot does not
//    entry    market, no later than MaxArmMin minutes after the anchor
//    exit     server-side stop at Stop, server-side limit at TargetA
//    Fridays are skipped and everything is flat by the weekend.
//
//  ALL DISTANCES ARE PRICE DOLLARS. Gold 4000 -> 4010 = 10.00.
//
//  >>> THE ONE THING THAT WILL SILENTLY BREAK THIS <<<
//  MT5 anchors are 09:55 / 16:15 on the BROKER's server clock, GMT+2 in
//  winter and GMT+3 in summer. cTrader's Server.Time is UTC. Leave
//  ServerOffsetHours at 0 and every anchor lands two or three hours away
//  from the MT5 one — a different strategy that still produces plausible
//  numbers. Set it to the offset your MT5 broker actually uses (3 in
//  summer, 2 in winter) or set UseUtcAnchors and give UTC times directly.
//
//  MT5 REFERENCE, XAUUSD, 2026.01.01–2026.08.23, $50k, 3% risk:
//      net $28,991   38 trades   92.11% win   maxDD 3.02%   PF 8.36
//  A cTrader run should land near this. A large gap means the clock.
// ---------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Net.Http;
using System.Threading.Tasks;
using cAlgo.API;
using cAlgo.API.Internals;

namespace cAlgo.Robots
{
    [Robot(TimeZone = TimeZones.UTC)]
    public class AureonAxisA8 : Robot
    {
        // ---- sessions -----------------------------------------------------
        [Parameter("Server offset from UTC (hours)", Group = "Clock", DefaultValue = 3)]
        public int ServerOffsetHours { get; set; }

        [Parameter("Use London", Group = "Sessions", DefaultValue = true)]
        public bool UseLondon { get; set; }
        [Parameter("London anchor HH:MM", Group = "Sessions", DefaultValue = "09:55")]
        public string LonAnchor { get; set; }
        [Parameter("London end HH:MM", Group = "Sessions", DefaultValue = "16:00")]
        public string LonEnd { get; set; }

        [Parameter("Use NY", Group = "Sessions", DefaultValue = true)]
        public bool UseNY { get; set; }
        [Parameter("NY anchor HH:MM", Group = "Sessions", DefaultValue = "16:15")]
        public string NyAnchor { get; set; }
        [Parameter("NY end HH:MM", Group = "Sessions", DefaultValue = "23:50")]
        public string NyEnd { get; set; }

        [Parameter("Anchor tolerance (min)", Group = "Sessions", DefaultValue = 5)]
        public int AnchorTolMin { get; set; }

        // ---- mechanism ----------------------------------------------------
        [Parameter("Disp — pivot trigger ($)", Group = "Entry", DefaultValue = 15.0)]
        public double Disp { get; set; }
        [Parameter("Conf — direction trigger ($)", Group = "Entry", DefaultValue = 5.0)]
        public double Conf { get; set; }
        [Parameter("Max pivot overshoot ($)", Group = "Entry", DefaultValue = 22.0)]
        public double MaxPivotDisp { get; set; }
        [Parameter("Min elapsed (min)", Group = "Entry", DefaultValue = 30)]
        public int MinElapsed { get; set; }
        [Parameter("Pivot window (min)", Group = "Entry", DefaultValue = 60)]
        public int PivotWindow { get; set; }
        [Parameter("Max arm (min)", Group = "Entry", DefaultValue = 45)]
        public int MaxArmMin { get; set; }
        [Parameter("Min life left (min)", Group = "Entry", DefaultValue = 30)]
        public int MinLifeMin { get; set; }
        [Parameter("Skip Friday", Group = "Entry", DefaultValue = true)]
        public bool SkipFriday { get; set; }
        [Parameter("Friday cutoff hour", Group = "Entry", DefaultValue = 16)]
        public int FridayCutoff { get; set; }

        // ---- exits --------------------------------------------------------
        [Parameter("Target ($)", Group = "Exit", DefaultValue = 10.0)]
        public double TargetA { get; set; }
        [Parameter("Stop ($)", Group = "Exit", DefaultValue = 20.0)]  // StopDist: named so it cannot hide Robot.Stop()
        public double StopDist { get; set; }
        [Parameter("Max chips per session", Group = "Exit", DefaultValue = 1)]
        public int MaxChips { get; set; }
        [Parameter("Session target ($)", Group = "Exit", DefaultValue = 10.0)]
        public double SessionTarget { get; set; }

        // ---- risk ---------------------------------------------------------
        [Parameter("Risk % per trade", Group = "Risk", DefaultValue = 3.0)]
        public double RiskPct { get; set; }
        [Parameter("Compound", Group = "Risk", DefaultValue = true)]
        public bool Compound { get; set; }
        [Parameter("Base equity (if not compounding)", Group = "Risk", DefaultValue = 50000.0)]
        public double BaseEquity { get; set; }
        [Parameter("Day loss brake %", Group = "Risk", DefaultValue = 3.0)]
        public double DayLossPct { get; set; }
        [Parameter("Max spread ($)", Group = "Risk", DefaultValue = 0.60)]
        public double MaxSpread { get; set; }

        // ---- account safeguards -------------------------------------------
        [Parameter("Max total drawdown % (0=off)", Group = "Safeguard", DefaultValue = 10.0)]
        public double MaxTotalDD { get; set; }
        [Parameter("Friday hard flat hour", Group = "Safeguard", DefaultValue = 21)]
        public int HardFlatHour { get; set; }
        [Parameter("Friday hard flat minute", Group = "Safeguard", DefaultValue = 30)]
        public int HardFlatMin { get; set; }
        [Parameter("Stale position minutes (0=off)", Group = "Safeguard", DefaultValue = 600)]
        public int StaleMinutes { get; set; }

        // Prop rules. Daily loss is measured against the balance at the START
        // of the day, which is how prop firms measure it - not against a
        // running equity peak.
        // PROFIT TARGET, two modes.
        //   HaltOnTarget = true   CHALLENGE: reach it, flatten, stop dead.
        //   HaltOnTarget = false  FUNDED:    reach it, flatten, BANK the
        //                         cycle, reset the baseline to the new
        //                         equity, carry on toward the next payout.
        //   Each cycle is a withdrawal. The baseline resets so the next
        //   target is measured from where the last one ended, not from the
        //   original deposit — otherwise the second cycle needs half the
        //   move of the first and the third needs a third.
        [Parameter("Profit target % (0=off)", Group = "Safeguard", DefaultValue = 40.0)]
        public double ProfitTargetPct { get; set; }
        [Parameter("Halt on target (false = bank and continue)", Group = "Safeguard", DefaultValue = false)]
        public bool HaltOnTarget { get; set; }
        [Parameter("Cooldown days after a payout", Group = "Safeguard", DefaultValue = 0)]
        public int CooldownDays { get; set; }

        // ---- Discord ------------------------------------------------------
        //  OUT  a webhook POST. No URL whitelist and no error 4014 - cTrader
        //       has no equivalent of the MT5 WebRequest restriction.
        //  IN   a bot polling GET /channels/{id}/messages. Needs a bot in the
        //       server with Read Messages + MESSAGE CONTENT INTENT enabled.
        //  Backtests skip all of it: RunningMode is checked once at start and
        //  nothing is sent, so a 200,000-bar run does not fire HTTP calls.
        //  Sends are fire-and-forget on a background task. A failed send is
        //  swallowed and can never touch trading logic.
        [Parameter("Discord on", Group = "Discord", DefaultValue = true)]
        public bool DiscordOn { get; set; }
        [Parameter("Webhook URL", Group = "Discord", DefaultValue = "")]
        public string WebhookUrl { get; set; }
        [Parameter("Notify entries", Group = "Discord", DefaultValue = true)]
        public bool NotifyEntry { get; set; }
        [Parameter("Notify exits", Group = "Discord", DefaultValue = true)]
        public bool NotifyExit { get; set; }
        [Parameter("Notify session anchors", Group = "Discord", DefaultValue = false)]
        public bool NotifySession { get; set; }
        [Parameter("Notify alerts", Group = "Discord", DefaultValue = true)]
        public bool NotifyAlerts { get; set; }
        [Parameter("Commands on", Group = "Discord", DefaultValue = false)]
        public bool CommandsOn { get; set; }
        [Parameter("Bot token", Group = "Discord", DefaultValue = "")]
        public string BotToken { get; set; }
        [Parameter("Channel ID", Group = "Discord", DefaultValue = "")]
        public string ChannelId { get; set; }
        [Parameter("Allowed user IDs (comma sep)", Group = "Discord", DefaultValue = "")]
        public string AllowedUserIds { get; set; }
        [Parameter("Poll seconds", Group = "Discord", DefaultValue = 10)]
        public int PollSeconds { get; set; }
        [Parameter("Heartbeat hours (0=off)", Group = "Discord", DefaultValue = 12)]
        public int HeartbeatHours { get; set; }
        [Parameter("Daily summary", Group = "Discord", DefaultValue = true)]
        public bool DailySummary { get; set; }

        // ---- plumbing -----------------------------------------------------
        [Parameter("Label", Group = "Misc", DefaultValue = "AX_A8")]
        public string Lbl { get; set; }
        [Parameter("Write decision journal", Group = "Misc", DefaultValue = true)]
        public bool JournalOn { get; set; }
        [Parameter("Journal folder", Group = "Misc", DefaultValue = @"C:\Users\Public\aureon")]
        public string JournalDir { get; set; }

        // -------------------------------------------------------------------
        private class Sess
        {
            public string Name;
            public int AnchorSec, EndSec;
            public bool Live, Done;
            public double Anchor, Ref, Pivot;
            public int PivotSec;
            public bool PivotOn;
            public double Secured;
            public int Chips;
        }

        private readonly List<Sess> _s = new List<Sess>();
        // The owning session is tracked by POSITION ID. Matching on Comment
        // silently failed in backtest: own came back null, Chips never
        // incremented, and 9 sessions took a second trade with MaxChips=1.
        private int _ownerIdx = -1;
        private long _ownerId = -1;

        private DateTime _day = DateTime.MinValue;
        private double _dayStartEq;
        private bool _dayHalt;
        private long _seq;
        private string _jPath;
        private int _sessTotal, _sessPivot, _sessTraded, _skipSpread;
        private double _slipSum; private int _slipN;
        private double _commSum; private int _commN;
        private double _peakEq, _startEq;
        private bool _stopped;
        private int _cycles;
        private double _banked;
        private DateTime _resumeOn = DateTime.MinValue;

        private static readonly HttpClient _http = new HttpClient();
        private bool _net;
        private bool _paused;
        private string _lastMsgId = "";
        private DateTime _nextPoll = DateTime.MinValue;
        private int _wins, _losses;
        private double _netTotal;
        private DateTime _nextBeat = DateTime.MinValue;
        private DateTime _bootTime;
        private bool _adopted;
        private int _dayTrades;
        // Why an entry did not happen. Without these a zero-trade run is
        // indistinguishable from a broken one.
        private int _blkPos, _blkPaused, _blkHalt, _blkCool, _blkChips,
                    _blkSecured, _blkLife, _blkEarly, _blkLate, _blkFriday,
                    _blkWindow, _blkDisp, _blkOvershoot, _blkNoConf;
        private double _bestDisp = double.MinValue;
        private double _bestConf;
        private double _spreadCost;   // spread paid at entry, account currency

        // ===================================================================
        protected override void OnStart()
        {
            if (UseLondon)
                _s.Add(new Sess { Name = "LONDON", AnchorSec = Hhmm(LonAnchor), EndSec = Hhmm(LonEnd) });
            if (UseNY)
                _s.Add(new Sess { Name = "NY", AnchorSec = Hhmm(NyAnchor), EndSec = Hhmm(NyEnd) });
            if (_s.Count == 0) { Print("no sessions enabled"); Stop(); return; }

            foreach (var s in _s) Reset(s);

            Print("==============================================================");
            Print("  AUREON AXIS A8 (cTrader)  {0}", SymbolName);
            Print("  CLOCK  server offset {0}h from UTC — anchors {1} / {2} in that clock",
                  ServerOffsetHours, LonAnchor, NyAnchor);
            Print("  ENTRY  disp {0} conf {1} window {2}-{3} min, up-pivots only",
                  Disp, Conf, MinElapsed, MaxArmMin);
            Print("  EXIT   target {0}  stop {1}  maxchips {2}", TargetA, StopDist, MaxChips);
            Print("  RISK   {0}% per trade, compound {1}, day brake {2}%",
                  RiskPct, Compound, DayLossPct);
            Print("  GUARD  daily {0}%, total DD {1}%, Friday flat {2:00}:{3:00}",
                  DayLossPct, MaxTotalDD, HardFlatHour, HardFlatMin);
            Print("  TARGET {0}% — {1}", ProfitTargetPct,
                  ProfitTargetPct <= 0 ? "OFF"
                  : HaltOnTarget ? "CHALLENGE: halt on reaching it"
                  : string.Format("FUNDED: bank and continue, cooldown {0}d", CooldownDays));
            if (DayLossPct >= RiskPct * 2)
                Print("  *** day brake {0}% allows TWO full {1}% losses. A 5% prop "
                    + "daily limit would be breached. ***", DayLossPct, RiskPct);
            Print("  pip size {0}, tick {1}, lot step {2}",
                  Symbol.PipSize, Symbol.TickSize, Symbol.VolumeInUnitsStep);
            if (ServerOffsetHours == 0)
                Print("  *** offset is 0. If the MT5 anchors were on a GMT+2/+3 broker "
                    + "clock this is NOT the same strategy. ***");
            Print("==============================================================");

            Positions.Closed += OnPosClosed;
            _peakEq = Account.Equity;
            _startEq = Account.Equity;
            _dayStartEq = Account.Equity;      // so the brake works on day one
            AdoptOrphan();

            _net = RunningMode == RunningMode.RealTime;
            if (DiscordOn && !_net)
                Print("  [discord] backtest detected - notifications disabled for this run");

            if (JournalOn) OpenJournal();

            _bootTime = Server.Time;
            _nextBeat = Server.Time.AddHours(Math.Max(1, HeartbeatHours));
            if (_net && DiscordOn && NotifyAlerts)
            {
                var open = Find();
                DSend(string.Format("{0} **AUREON AXIS A8** {1}\n" +
                      "equity {2:F2} | balance {3:F2} | risk {4}%\n" +
                      "disp {5} conf {6} | stop {7} target {8}\n" +
                      "guards: daily {9}%, DD {10}%, payout {11}% ({12})\n{13}",
                      open != null ? "**RESTARTED**" : "**STARTED**",
                      SymbolName, Account.Equity, Account.Balance, RiskPct,
                      Disp, Conf, StopDist, TargetA,
                      DayLossPct, MaxTotalDD, ProfitTargetPct,
                      HaltOnTarget ? "halt" : "bank and continue",
                      open != null
                        ? string.Format("adopted open position: {0} {1} units @ {2:F2}, stop {3:F2}",
                                        open.TradeType, open.VolumeInUnits, open.EntryPrice,
                                        open.StopLoss.GetValueOrDefault())
                        : "no open position"));
            }
        }

        protected override void OnStop()
        {
            Print("---------------------- diagnostics ----------------------");
            Print("  sessions anchored {0}, pivoted {1}, traded {2}",
                  _sessTotal, _sessPivot, _sessTraded);
            Print("  entries skipped on spread: {0}", _skipSpread);
            if (_cycles > 0)
                Print("  PAYOUTS {0} at {1}% each, {2:F2} banked in total",
                      _cycles, ProfitTargetPct, _banked);
            if (_slipN > 0)
                Print("  mean entry slippage {0:F3} price $ over {1} fills",
                      _slipSum / _slipN, _slipN);
            Print("  --- COSTS ---");
            if (_commN > 0)
                Print("    commission {0:F2} total, {1:F2} per trade (cTrader charges this;"
                    + " the MT5 build models it via InpCommPerLot)",
                      _commSum, _commSum / _commN);
            Print("    spread paid at entry: {0:F2}", _spreadCost);
            Print("    TOTAL COST          : {0:F2}", Math.Abs(_commSum) + _spreadCost);
            Print("  --- why entries were blocked (bar counts) ---");
            Print("    position open {0} | paused {1} | halted {2} | cooldown {3}",
                  _blkPos, _blkPaused, _blkHalt, _blkCool);
            Print("    chips used {0} | session secured {1} | too little life left {2}",
                  _blkChips, _blkSecured, _blkLife);
            Print("    before MinElapsed {0} | after MaxArmMin {1} | Friday {2}",
                  _blkEarly, _blkLate, _blkFriday);
            Print("    past PivotWindow {0} | disp below {1} ({2}) | overshoot rejected {3}",
                  _blkWindow, Disp, _blkDisp, _blkOvershoot);
            Print("    pivot set but no confirmation {0}", _blkNoConf);
            Print("  --- what the market actually offered ---");
            Print("    largest displacement seen from ref : {0:F5}   (Disp is {1})",
                  _bestDisp == double.MinValue ? 0 : _bestDisp, Disp);
            Print("    largest move from a pivot          : {0:F5}   (Conf is {1})",
                  _bestConf, Conf);
            Print("    symbol: pip {0}, tick {1}, digits {2}, min volume {3}",
                  Symbol.PipSize, Symbol.TickSize, Symbol.Digits, Symbol.VolumeInUnitsMin);
            Print("---------------------------------------------------------");
        }

        // ===================================================================
        //  SAFEGUARDS run on every tick, driven by the clock. OnBar only
        //  fires when a new bar forms, so anything that depends on it can be
        //  defeated by the broker going quiet. That is exactly how a Friday
        //  NY position survived to Monday in the MT5 build and gapped -34.74
        //  against a 20 stop.
        // ===================================================================
        protected override void OnTick()
        {
            Poll();
            Heartbeat();

            var p = Find();
            DateTime now = Server.Time.AddHours(ServerOffsetHours);

            // ---- EQUITY GUARDS: these must run whether or not a position
            // ---- is open. Equity crosses the target at the instant a
            // ---- winning trade closes, and from that tick on there is no
            // ---- position - so gating them behind "p == null" meant the
            // ---- payout never fired, and then flattened the NEXT trade
            // ---- instead. Max drawdown had the same hole while flat.
            if (Account.Equity > _peakEq) _peakEq = Account.Equity;

            if (ProfitTargetPct > 0 && !_stopped && _startEq > 0)
            {
                double gain = (Account.Equity - _startEq) / _startEq * 100.0;
                if (gain >= ProfitTargetPct)
                {
                    if (p != null) ClosePos(p, "TARGET_HIT", now, null);
                    if (HaltOnTarget)
                    {
                        _stopped = true;
                        Print("PROFIT TARGET +{0:F2}% — flattened and HALTED (challenge)", gain);
                    }
                    else
                    {
                        _cycles++;
                        double amount = Account.Equity - _startEq;
                        _banked += amount;
                        _startEq = Account.Equity;      // baseline moves up
                        _peakEq  = Account.Equity;      // drawdown measured afresh
                        if (CooldownDays > 0) _resumeOn = Server.Time.AddDays(CooldownDays);
                        Print("PAYOUT {0}: +{1:F2}% = {2:F2}. banked {3:F2} total. "
                            + "new baseline {4:F2}", _cycles, gain, amount, _banked, _startEq);
                        DSend(string.Format("**PAYOUT {0}** {1}\n+{2:F2}% = {3:F2}\n" +
                              "banked {4:F2} total | new baseline {5:F2}",
                              _cycles, SymbolName, gain, amount, _banked, _startEq));
                    }
                    return;
                }
            }

            if (MaxTotalDD > 0 && _peakEq > 0 && !_stopped)
            {
                double dd = (_peakEq - Account.Equity) / _peakEq * 100.0;
                if (dd >= MaxTotalDD)
                {
                    _stopped = true;
                    Print("MAX DRAWDOWN {0:F2}% - flattening and halting", dd);
                    if (NotifyAlerts)
                        DSend(string.Format("**MAX DRAWDOWN** {0} -{1:F2}% - flattened and HALTED",
                              SymbolName, dd));
                    if (p != null) ClosePos(p, "MAX_DD", now, null);
                    return;
                }
            }

            // ---- POSITION GUARDS: only meaningful with something open ----
            if (p == null) return;

            // Friday hard flatten, whatever the broker is quoting
            if (now.DayOfWeek == DayOfWeek.Friday
                && (now.Hour > HardFlatHour
                    || (now.Hour == HardFlatHour && now.Minute >= HardFlatMin)))
            {
                Print("Friday hard flatten at {0:HH:mm}", now);
                ClosePos(p, "FRIDAY_FLAT", now, null);
                return;
            }

            // a position with no stop must not be carried
            if (p.StopLoss == null)
            {
                Print("position has NO STOP - flattening");
                ClosePos(p, "NO_STOP", now, null);
                return;
            }

            // stale position, e.g. after a restart lost session ownership
            if (StaleMinutes > 0 && _ownerId != p.Id)
            {
                double held = (Server.Time - p.EntryTime).TotalMinutes;
                if (held >= StaleMinutes)
                {
                    Print("orphan position held {0:F0} min - flattening", held);
                    ClosePos(p, "STALE", now, null);
                }
            }
        }

        //  A restart clears _ownerId, so an open position would never be
        //  closed at its session end. Re-attach to it instead.
        private void AdoptOrphan()
        {
            var p = Find();
            if (p == null) return;
            _ownerId = p.Id;
            _ownerIdx = -1;                 // session unknown; OnTick guards it
            Print("adopted orphan position {0} {1} {2} units @ {3:F2}",
                  p.Id, p.TradeType, p.VolumeInUnits, p.EntryPrice);
        }

        // ===================================================================
        //  All decisions are taken on the CLOSE of a completed M1 bar.
        //  In OnBar the freshly opened bar is index 0, so index 1 is the bar
        //  that just closed. Reading index 0 here would be lookahead.
        // ===================================================================
        protected override void OnBar()
        {
            int last = Bars.Count - 2;              // last CLOSED bar
            if (last < 5) return;

            double c = Bars.ClosePrices[last];
            DateTime bar = Bars.OpenTimes[last].AddHours(ServerOffsetHours);
            int sec = bar.Hour * 3600 + bar.Minute * 60;
            DateTime day = bar.Date;

            if (day != _day)
            {
                if (_net && DiscordOn && DailySummary && _day != DateTime.MinValue
                    && _dayTrades > 0)
                    DSend(string.Format("daily {0} {1:yyyy-MM-dd}: {2} trades, {3:+0.00;-0.00}\n" +
                          "equity {4:F2} | cycle {5:+0.00;-0.00}% of {6}%",
                          SymbolName, _day, _dayTrades, Account.Equity - _dayStartEq,
                          Account.Equity,
                          _startEq > 0 ? (Account.Equity - _startEq) / _startEq * 100.0 : 0,
                          ProfitTargetPct));
                _dayTrades = 0;
                CloseAll("ROLLOVER", bar);
                _day = day;
                _dayHalt = false;
                _dayStartEq = Account.Equity;
                foreach (var s in _s) Reset(s);
            }

            if (DayLossPct > 0 && !_dayHalt && _dayStartEq > 0)
            {
                double dd = (_dayStartEq - Account.Equity) / _dayStartEq * 100.0;
                if (dd >= DayLossPct)
                {
                    _dayHalt = true;
                    CloseAll("DAY_BRAKE", bar);
                    Print("[{0}] day brake at -{1:F2}%", bar, dd);
                    if (NotifyAlerts)
                        DSend(string.Format("**DAY BRAKE** {0} at -{1:F2}% - no more trades today",
                              SymbolName, dd));
                }
            }

            var pos = Find();

            foreach (var s in _s)
            {
                int endsec = s.EndSec;

                // ---- anchor ------------------------------------------------
                if (!s.Live && !s.Done)
                {
                    if (sec >= s.AnchorSec && sec <= s.AnchorSec + AnchorTolMin * 60 && sec < endsec)
                    {
                        s.Live = true; s.Anchor = c; s.Ref = c; _sessTotal++;
                        J(bar, "ANCHOR", s.Name, c, c, c, 0, 0, "session opened");
                        if (NotifySession)
                            DSend(string.Format("{0} {1} anchor {2:F2} - watching for +{3}",
                                  SymbolName, s.Name, c, Disp));
                    }
                    continue;
                }
                if (!s.Live) continue;

                // ---- session over -----------------------------------------
                if (sec >= endsec)
                {
                    if (pos != null && _ownerIdx == _s.IndexOf(s))
                    {
                        ClosePos(pos, "SESSION_END", bar, s);
                        pos = null;
                    }
                    s.Live = false; s.Done = true;
                    continue;
                }

                if (pos != null) { _blkPos++; continue; }
                if (_paused) { _blkPaused++; continue; }
                if (_dayHalt || _stopped) { _blkHalt++; continue; }
                if (_resumeOn > DateTime.MinValue && Server.Time < _resumeOn) { _blkCool++; continue; }
                if (s.Chips >= MaxChips) { _blkChips++; continue; }
                if (s.Secured >= SessionTarget - 1e-9) { _blkSecured++; continue; }
                if ((endsec - sec) / 60.0 < MinLifeMin) { _blkLife++; continue; }
                if ((sec - s.AnchorSec) / 60.0 < MinElapsed) { _blkEarly++; continue; }
                if (MaxArmMin > 0 && (sec - s.AnchorSec) / 60.0 > MaxArmMin) { _blkLate++; continue; }
                if (SkipFriday && bar.DayOfWeek == DayOfWeek.Friday && bar.Hour >= FridayCutoff)
                    { _blkFriday++; continue; }

                // ---- stage 1: pivot, up-moves only -------------------------
                if (!s.PivotOn)
                {
                    if ((sec - s.AnchorSec) / 60.0 > PivotWindow) { _blkWindow++; continue; }
                    double d = c - s.Ref;
                    if (d > _bestDisp) _bestDisp = d;
                    if (d < Disp) { _blkDisp++; continue; }
                    if (MaxPivotDisp > 0 && d > MaxPivotDisp)
                    {
                        J(bar, "SKIP_OVERSHOOT", s.Name, c, s.Anchor, s.Ref, 0,
                          (sec - s.AnchorSec) / 60, string.Format("disp {0:F2} > {1:F2}", d, MaxPivotDisp));
                        _blkOvershoot++;
                        s.Ref = c;
                        continue;
                    }
                    s.PivotOn = true; s.Pivot = c; s.PivotSec = sec;
                    if (s.Chips == 0) _sessPivot++;
                    J(bar, "PIVOT", s.Name, c, s.Anchor, s.Ref, c,
                      (sec - s.AnchorSec) / 60, string.Format("disp {0:F2}", d));
                    continue;
                }

                // ---- stage 2: the market picks the side --------------------
                double mv = c - s.Pivot;
                int dir = 0;
                if (Math.Abs(mv) > _bestConf) _bestConf = Math.Abs(mv);
                if (mv >= Conf) dir = 1;
                if (mv <= -Conf) dir = -1;
                if (dir == 0) { _blkNoConf++; continue; }

                J(bar, "CONFIRM", s.Name, c, s.Anchor, s.Ref, s.Pivot,
                  (sec - s.AnchorSec) / 60,
                  string.Format("{0} move {1:F2}", dir > 0 ? "LONG" : "SHORT", mv));

                Open(s, dir, bar, sec);
                pos = Find();
            }
        }

        // ===================================================================
        private void Open(Sess s, int dir, DateTime bar, int sec)
        {
            double spr = Symbol.Ask - Symbol.Bid;
            if (MaxSpread > 0 && spr > MaxSpread)
            {
                _skipSpread++;
                J(bar, "SKIP_SPREAD", s.Name, 0, s.Anchor, s.Ref, s.Pivot,
                  (sec - s.AnchorSec) / 60, string.Format("spread {0:F2}", spr));
                return;
            }

            // Sizing via PipValue so this works on ANY symbol, not just gold.
            // On XAUUSD (quote USD, 1 unit = 1 oz) it reduces to
            // units = risk / stopDist, the same arithmetic as the MT5 build.
            // On USDJPY the P/L is in JPY, so the naive formula would be
            // wrong by the USDJPY rate - PipValue handles the conversion.
            double eq = Compound ? Account.Equity : BaseEquity;
            double risk = eq * RiskPct / 100.0;
            double stopPips = StopDist / Symbol.PipSize;
            double perUnit = stopPips * Symbol.PipValue;
            if (perUnit <= 0) { Print("cannot value the stop - entry skipped"); return; }
            double units = Symbol.NormalizeVolumeInUnits(risk / perUnit, RoundingMode.Down);
            if (units < Symbol.VolumeInUnitsMin)
            {
                Print("volume rounds below minimum — entry skipped");
                return;
            }

            double req = dir > 0 ? Symbol.Ask : Symbol.Bid;

            // Open BARE, then attach the stop and target as absolute PRICES.
            // Converting dollars to pips gave exits at 10-33 and -20 to -23
            // instead of a clean 10 / -20: PipSize on gold is not what the
            // conversion assumed. Prices remove the assumption entirely.
            var r = ExecuteMarketOrder(dir > 0 ? TradeType.Buy : TradeType.Sell,
                                       SymbolName, units, Lbl);
            if (!r.IsSuccessful)
            {
                Print("open failed: {0}", r.Error);
                return;
            }

            double fillPx = r.Position.EntryPrice;
            double slPx = Math.Round(fillPx - StopDist * dir, Symbol.Digits);
            double tpPx = Math.Round(fillPx + TargetA * dir, Symbol.Digits);
            // ProtectionType.Absolute: slPx/tpPx are PRICES, not distances.
            // The two-argument overload is obsolete.
            var m = ModifyPosition(r.Position, slPx, tpPx, ProtectionType.Absolute);
            if (!m.IsSuccessful)
            {
                Print("SL/TP rejected ({0}) - flattening", m.Error);
                if (NotifyAlerts)
                    DSend(string.Format("**STOP REJECTED** {0} ({1}) - position flattened",
                          SymbolName, m.Error));
                ClosePosition(r.Position);
                return;
            }

            _ownerId = r.Position.Id;
            _ownerIdx = _s.IndexOf(s);
            double fill = fillPx;
            double slip = (fill - req) * dir;
            _slipSum += slip; _slipN++;
            _spreadCost += spr * units * Symbol.PipValue / Symbol.PipSize;

            J(bar, "ENTRY", s.Name, 0, s.Anchor, s.Ref, s.Pivot,
              (sec - s.AnchorSec) / 60,
              string.Format("{0} req {1:F2} slip {2:+0.00;-0.00}",
                            dir > 0 ? "BUY" : "SELL", req, slip),
              fill, units);

            if (NotifyEntry)
                DSend(string.Format("**ENTRY** {0} {1} {2}\n{3} units @ {4:F2} | stop {5:F2} | target {6:F2}\n" +
                      "anchor {7:F2} | pivot {8:F2} | arm {9} min | slip {10:+0.00;-0.00}\n" +
                      "spread {11:F5} ({12:F2}) | equity {13:F2}",
                      SymbolName, s.Name, dir > 0 ? "BUY" : "SELL", units, fill, slPx, tpPx,
                      s.Anchor, s.Pivot, (sec - s.AnchorSec) / 60, slip,
                      spr, spr * units * Symbol.PipValue / Symbol.PipSize, Account.Equity));
            Print("[{0}] {1} {2} {3} units @ {4:F2}  pivot {5:F2}  sl {6:F2} tp {7:F2}",
                  bar, s.Name, dir > 0 ? "BUY" : "SELL", units, fill, s.Pivot,
                  slPx, tpPx);
        }

        private Position Find()
        {
            foreach (var p in Positions)
                if (p.Label == Lbl && p.SymbolName == SymbolName) return p;
            return null;
        }

        // closing fires Positions.Closed, which books it. _forced carries the
        // reason across so a session-end close is not mislabelled TARGET/STOP.
        private string _forced;

        private void ClosePos(Position p, string reason, DateTime bar, Sess s)
        {
            _forced = reason;
            ClosePosition(p);
        }

        private void CloseAll(string reason, DateTime bar)
        {
            var p = Find();
            if (p == null) return;
            ClosePos(p, reason, bar, null);
        }

        private void Book(Sess s, double entry, double exit, int dir,
                          string reason, DateTime bar, double units)
        {
            double move = (exit - entry) * dir;
            if (s != null)
            {
                if (s.Chips == 0) _sessTraded++;
                s.Secured += move;
                s.Chips++;
                s.Ref = exit;
                s.PivotOn = false;
            }
            J(bar, "EXIT", s != null ? s.Name : "?", 0,
              s != null ? s.Anchor : 0, s != null ? s.Ref : 0,
              s != null ? s.Pivot : 0, 0,
              string.Format("{0} move {1:+0.00;-0.00}", reason, move), exit, units);
            Print("[{0}] {1} {2} {3:+0.00;-0.00} price $   secured {4:F2}",
                  bar, s != null ? s.Name : "?", reason, move,
                  s != null ? s.Secured : 0);
        }

        // ---- positions closed by the server-side SL/TP -------------------
        //  Position has no ClosePrice in the current API and the
        //  OnPositionClosed override is obsolete, so this subscribes to the
        //  event and reads the fill out of History. This is the ONLY place a
        //  trade is booked - ClosePos just closes and lets this run, so a
        //  manual close cannot be counted twice.
        private void OnPosClosed(PositionClosedEventArgs args)
        {
            var p = args.Position;
            if (p.Label != Lbl || p.SymbolName != SymbolName) return;

            double exit = 0;
            var h = History.FindLast(Lbl, SymbolName, p.TradeType);
            if (h != null) exit = h.ClosingPrice;
            if (exit <= 0) exit = p.TradeType == TradeType.Buy ? Symbol.Bid : Symbol.Ask;

            Sess own = (p.Id == _ownerId && _ownerIdx >= 0 && _ownerIdx < _s.Count)
                       ? _s[_ownerIdx] : null;
            _ownerId = -1; _ownerIdx = -1;

            int dir = p.TradeType == TradeType.Buy ? 1 : -1;
            double move = (exit - p.EntryPrice) * dir;
            string reason = _forced ?? (move >= TargetA - 0.05 ? "TARGET"
                                      : move <= -StopDist + 0.05 ? "STOP" : "OTHER");
            _forced = null;

            DateTime bar = Server.Time.AddHours(ServerOffsetHours);
            Book(own, p.EntryPrice, exit, dir, reason, bar, p.VolumeInUnits);
            _commSum += p.Commissions; _commN++;
            _netTotal += p.NetProfit;
            if (p.NetProfit > 0) _wins++; else _losses++;
            _dayTrades++;
            if (NotifyExit)
                DSend(string.Format("{0} **{1}** {2} {3}\nmove {4:+0.00;-0.00} price $ | **{5:+0.00;-0.00}**\n" +
                      "commission {6:F2} | secured {7:F2}/{8}\nequity {9:F2} | day {10:+0.00;-0.00}",
                      p.NetProfit > 0 ? "[WIN] " : "[LOSS]", reason, SymbolName,
                      own != null ? own.Name : "?", move, p.NetProfit, p.Commissions,
                      own != null ? own.Secured : 0, SessionTarget,
                      Account.Equity, Account.Equity - _dayStartEq));
            Print("   net {0:F2}  commission {1:F2}", p.NetProfit, p.Commissions);
        }


        // ===================================================================
        //  DISCORD
        // ===================================================================
        private void DSend(string text)
        {
            if (!_net || !DiscordOn || string.IsNullOrWhiteSpace(WebhookUrl)) return;
            string body = "{\"content\":\"" + JEsc(text) + "\"}";
            Task.Run(async () =>
            {
                try
                {
                    var content = new StringContent(body, Encoding.UTF8, "application/json");
                    await _http.PostAsync(WebhookUrl, content);
                }
                catch { }
            });
        }

        private static string JEsc(string t)
        {
            return t.Replace("\\", "\\\\").Replace("\"", "\\\"")
                    .Replace("\r", "").Replace("\n", "\\n");
        }

        private string StateLine()
        {
            var sb = new StringBuilder();
            sb.AppendFormat("**{0}**  equity {1:F2} | balance {2:F2}\n",
                            SymbolName, Account.Equity, Account.Balance);
            sb.AppendFormat("risk {0}% | {1}\n", RiskPct, _paused ? "PAUSED" : "active");
            if (_stopped) sb.Append("HALTED\n");
            var p = Find();
            if (p != null)
                sb.AppendFormat("open: {0} {1} units @ {2:F2}, stop {3:F2}, now {4:+0.00;-0.00}\n",
                                p.TradeType, p.VolumeInUnits, p.EntryPrice,
                                p.StopLoss.GetValueOrDefault(), p.Pips * Symbol.PipSize);
            else sb.Append("no position\n");
            foreach (var s in _s)
                if (s.Live)
                    sb.AppendFormat("{0}: anchor {1:F2}, pivot {2}, secured {3:F2}, chips {4}\n",
                                    s.Name, s.Anchor, s.PivotOn ? "set" : "waiting",
                                    s.Secured, s.Chips);
            sb.AppendFormat("cycle {0:F2}% toward {1}%",
                            _startEq > 0 ? (Account.Equity - _startEq) / _startEq * 100.0 : 0,
                            ProfitTargetPct);
            return sb.ToString();
        }

        private bool AllowedUser(string uid)
        {
            if (string.IsNullOrWhiteSpace(AllowedUserIds)) return true;
            return AllowedUserIds.Contains(uid);
        }

        private void DoCommand(string raw, string uid)
        {
            if (raw == null) return;
            string cmd = raw.Trim().ToLowerInvariant();
            if (cmd.Length == 0 || cmd[0] != '!') return;
            if (!AllowedUser(uid)) { DSend("not authorised"); return; }

            if (cmd == "!help")
                DSend("`!status` state and open position\n" +
                      "`!pause` stop opening new trades\n" +
                      "`!resume` allow new trades\n" +
                      "`!close` flatten now\n" +
                      "`!today` day P/L\n" +
                      "`!stats` totals and payouts\n" +
                      "`!guards` current limits");
            else if (cmd == "!status") DSend(StateLine());
            else if (cmd == "!pause") { _paused = true; DSend("paused - no new entries"); }
            else if (cmd == "!resume") { _paused = false; DSend("resumed"); }
            else if (cmd == "!close")
            {
                var p = Find();
                if (p == null) DSend("nothing open");
                else
                {
                    ClosePos(p, "DISCORD", Server.Time.AddHours(ServerOffsetHours), null);
                    DSend("flattened by command");
                }
            }
            else if (cmd == "!today")
                DSend(string.Format("day P/L {0:F2} | equity {1:F2} | {2}",
                      Account.Equity - _dayStartEq, Account.Equity,
                      _dayHalt ? "DAY HALTED" : "running"));
            else if (cmd == "!stats")
                DSend(string.Format("trades {0} | wins {1} losses {2} | net {3:F2}\n" +
                      "sessions {4}, pivoted {5}, traded {6}\npayouts {7}, banked {8:F2}",
                      _wins + _losses, _wins, _losses, _netTotal,
                      _sessTotal, _sessPivot, _sessTraded, _cycles, _banked));
            else if (cmd == "!guards")
                DSend(string.Format("daily {0}% | total DD {1}% | target {2}% ({3})\n" +
                      "risk {4}% | stop {5} | target {6}",
                      DayLossPct, MaxTotalDD, ProfitTargetPct,
                      HaltOnTarget ? "halt" : "bank and continue",
                      RiskPct, StopDist, TargetA));
            else DSend("unknown command - try `!help`");
        }

        //  cTrader has no JSON parser available to cBots, so this pulls the
        //  three fields it needs out of the raw payload. Discord orders each
        //  message as id, type, content, then author.
        private static string Field(string src, string key, int from, out int end)
        {
            end = -1;
            int k = src.IndexOf("\"" + key + "\":\"", from, StringComparison.Ordinal);
            if (k < 0) return "";
            int a = k + key.Length + 4;
            int b = a;
            while (b < src.Length)
            {
                if (src[b] == '\\') { b += 2; continue; }
                if (src[b] == '"') break;
                b++;
            }
            end = b;
            return src.Substring(a, b - a);
        }

        //  Periodic proof-of-life. Without it a silent bot and a dead bot
        //  look identical from Discord.
        private void Heartbeat()
        {
            if (!_net || !DiscordOn || HeartbeatHours <= 0) return;
            if (Server.Time < _nextBeat) return;
            _nextBeat = Server.Time.AddHours(HeartbeatHours);
            var p = Find();
            DSend(string.Format("heartbeat {0} | up {1:F1}h | equity {2:F2} | " +
                  "cycle {3:+0.00;-0.00}% of {4}% | trades {5} ({6}W/{7}L) | {8}",
                  SymbolName, (Server.Time - _bootTime).TotalHours, Account.Equity,
                  _startEq > 0 ? (Account.Equity - _startEq) / _startEq * 100.0 : 0,
                  ProfitTargetPct, _wins + _losses, _wins, _losses,
                  p != null ? "in a trade" : (_paused ? "paused" : "flat")));
        }

        private void Poll()
        {
            if (!_net || !CommandsOn) return;
            if (string.IsNullOrWhiteSpace(BotToken) || string.IsNullOrWhiteSpace(ChannelId)) return;
            if (Server.Time < _nextPoll) return;
            _nextPoll = Server.Time.AddSeconds(Math.Max(5, PollSeconds));

            string url = "https://discord.com/api/v10/channels/" + ChannelId +
                         "/messages?limit=5";
            if (_lastMsgId != "") url += "&after=" + _lastMsgId;

            Task.Run(async () =>
            {
                string body;
                try
                {
                    var req = new HttpRequestMessage(System.Net.Http.HttpMethod.Get, url);
                    req.Headers.TryAddWithoutValidation("Authorization", "Bot " + BotToken);
                    var res = await _http.SendAsync(req);
                    if (!res.IsSuccessStatusCode) return;
                    body = await res.Content.ReadAsStringAsync();
                }
                catch { return; }

                var ids = new List<string>();
                var txt = new List<string>();
                var aut = new List<string>();
                int pos = 0;
                while (true)
                {
                    int cs = body.IndexOf("\"content\":\"", pos, StringComparison.Ordinal);
                    if (cs < 0) break;
                    int ce;
                    string content = Field(body, "content", pos, out ce);
                    if (ce < 0) break;

                    string mid = "";
                    int scan = 0, last = -1;
                    while (true)
                    {
                        int f = body.IndexOf("\"id\":\"", scan, StringComparison.Ordinal);
                        if (f < 0 || f > cs) break;
                        last = f; scan = f + 6;
                    }
                    if (last >= 0) { int te; mid = Field(body, "id", last, out te); }

                    string uid = "";
                    int ae = body.IndexOf("\"author\":{", ce, StringComparison.Ordinal);
                    if (ae >= 0) { int ue; uid = Field(body, "id", ae, out ue); }

                    ids.Add(mid); txt.Add(content); aut.Add(uid);
                    pos = ce + 1;
                    if (ids.Count >= 5) break;
                }
                for (int i = ids.Count - 1; i >= 0; i--)
                {
                    if (ids[i] != "") _lastMsgId = ids[i];
                    try { DoCommand(txt[i], aut[i]); } catch { }
                }
            });
        }

        // ===================================================================
        private void Reset(Sess s)
        {
            s.Live = false; s.Done = false; s.Anchor = 0; s.Ref = 0;
            s.Pivot = 0; s.PivotOn = false; s.PivotSec = 0;
            s.Secured = 0; s.Chips = 0;
        }

        private static int Hhmm(string t)
        {
            var p = t.Split(':');
            return int.Parse(p[0]) * 3600 + int.Parse(p[1]) * 60;
        }

        // ---- decision journal, same columns as the MT5 build -------------
        private void OpenJournal()
        {
            try
            {
                Directory.CreateDirectory(JournalDir);
                string mode = RunningMode == RunningMode.RealTime ? "LIVE" : "TESTER";
                _jPath = Path.Combine(JournalDir,
                          string.Format("aureon_A8_CTRADER_decisions_{0}.csv", mode));
                System.IO.File.WriteAllText(_jPath,
                    "seq,bar_time,event,session,bar_close,anchor,ref,pivot," +
                    "elapsed_min,detail,price,lot\n");
                Print("journal -> {0}", _jPath);
            }
            catch (Exception e) { Print("journal failed: {0}", e.Message); JournalOn = false; }
        }

        private void J(DateTime bar, string ev, string sess, double bclose,
                       double anchor, double rf, double pivot, int elapsed,
                       string detail, double price = 0, double lot = 0)
        {
            if (!JournalOn || _jPath == null) return;
            var sb = new StringBuilder();
            sb.Append(++_seq).Append(',')
              .Append(bar.ToString("yyyy.MM.dd HH:mm", CultureInfo.InvariantCulture)).Append(',')
              .Append(ev).Append(',').Append(sess).Append(',')
              .Append(bclose.ToString("F2", CultureInfo.InvariantCulture)).Append(',')
              .Append(anchor.ToString("F2", CultureInfo.InvariantCulture)).Append(',')
              .Append(rf.ToString("F2", CultureInfo.InvariantCulture)).Append(',')
              .Append(pivot.ToString("F2", CultureInfo.InvariantCulture)).Append(',')
              .Append(elapsed).Append(',').Append(detail).Append(',')
              .Append(price.ToString("F2", CultureInfo.InvariantCulture)).Append(',')
              .Append(lot.ToString("F2", CultureInfo.InvariantCulture)).Append('\n');
            try { System.IO.File.AppendAllText(_jPath, sb.ToString()); } catch { }
        }
    }
}