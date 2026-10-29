using WindFlow.Core;

namespace WindFlow.App;

/// <summary>What the room chips mean, and where the numbers come from. The number graded is the room's FRESH-AIR ACH,
/// 3600 / mean age of air (RoomResult.FreshAch): outdoor air, with air that comes through other rooms credited at its real age.
/// The total air exchange (RoomResult.Ach) is shown beside it, never graded. Two questions get asked of a
/// ventilation rate and they have very different answers, so the panel asks which one you mean:
/// <list type="bullet">
/// <item><b>Air quality</b> — is there enough outdoor air for the people in the room? Tenths of an air
/// change per hour.</item>
/// <item><b>Cooling</b> — is there enough air movement to carry heat out? Several air changes per hour,
/// an order of magnitude more.</item>
/// </list>
/// <para><b>Every number below is either quoted from a named document or explicitly marked as a rule of
/// thumb.</b> Nothing here is invented. Where a published figure is not in ACH (most are in l/s per person
/// or cfm per square foot, because ACH hides the room volume) the conversion and its assumption are written
/// out, and the threshold is labelled as derived.</para></summary>
public enum AchIntent
{
    /// <summary>Outdoor air for occupants: the default, and what a whole-dwelling ventilation check means.</summary>
    AirQuality,
    /// <summary>Air movement for heat removal: what a cross-ventilation or night-purge design is aiming at.</summary>
    Cooling,
}

public enum AchBand { Poor, Weak, Good }

public static class Standards
{
    // ---------------------------------------------------------------- air quality

    /// <summary>ASHRAE Standard 62-1989 set the whole-house residential minimum at 0.35 air changes per hour
    /// (but not less than 15 cfm per person). It is the only mainstream ventilation requirement ever written
    /// directly as an ACH figure, which is why it is still quoted.
    /// <para><b>It has been superseded:</b> ASHRAE 62.2 now specifies a whole-building rate as a formula in
    /// cfm (floor area plus bedroom count), not as an air change rate, precisely because ACH depends on
    /// ceiling height. Treat 0.35 as a floor below which a dwelling is certainly under-ventilated, not as
    /// current compliance.</para></summary>
    public const float AirQualityPoor = 0.35f;

    public const string AirQualityPoorSource =
        "ASHRAE Standard 62-1989: whole-house residential minimum 0.35 ACH (and at least 15 cfm per person). " +
        "Superseded by ASHRAE 62.2, which specifies cfm rather than ACH.";

    /// <summary>DERIVED, not quoted. Per-person ventilation guidance (about 10 l/s per person for ordinary
    /// occupancy -- the CIBSE Guide A figure, and BS EN 16798-1 category I; category II is 7 l/s) converted to an air
    /// change rate at an assumed <see cref="VolumePerPerson"/>: <c>10 l/s * 3.6 / 30 m3 = 1.2 ACH</c>. The assumption
    /// is the weak link, so the tooltip shows it. At category II (7 l/s) the same 30 m3 would give 0.84 ACH.</summary>
    public const float AirQualityGood = 1.2f;

    /// <summary>The occupied volume per person the conversion above assumes, m3. A 30 m3 allowance is a
    /// typical living room or office figure; a crowded room needs proportionally more air changes for the
    /// same per-person rate, which is exactly the effect ACH hides.</summary>
    public const float VolumePerPerson = 30f;

    public const string AirQualityGoodSource =
        "Derived: 10 l/s per person of outdoor air (CIBSE Guide A; BS EN 16798-1 category I, category II is 7 l/s) at an " +
        "assumed 30 m³ per person gives 10 × 3.6 ÷ 30 = 1.2 ACH. Change the occupancy and this moves.";

    // ---------------------------------------------------------------- cooling

    /// <summary>RULE OF THUMB, not a standard. No published ventilation standard sets a cooling air change
    /// rate: the requirement is a temperature or a comfort criterion (CIBSE TM52/TM59 overheating, ASHRAE 55
    /// elevated air speed), and the ACH that satisfies it depends on the gains, the outdoor temperature and
    /// the room. The band below is the range designers commonly quote for a cross-ventilated dwelling doing
    /// useful daytime cooling; it is a sanity check on the design, not a compliance test.</summary>
    public const float CoolingPoor = 2f;
    public const float CoolingGood = 4f;

    public const string CoolingSource =
        "Rule of thumb, not a standard: 4+ ACH is the range commonly quoted for cross-ventilation doing " +
        "useful daytime cooling. The real criterion is a comfort one (CIBSE TM52/TM59 overheating, ASHRAE 55 " +
        "elevated air speed), which depends on the gains and the outdoor temperature, not on ACH alone.";

    // ----------------------------------------------------------------

    public static AchBand Band(float ach, AchIntent intent)
    {
        float good = intent == AchIntent.Cooling ? CoolingGood : AirQualityGood;
        float poor = intent == AchIntent.Cooling ? CoolingPoor : AirQualityPoor;
        return ach >= good ? AchBand.Good : ach >= poor ? AchBand.Weak : AchBand.Poor;
    }

    /// <summary>The verdict the room dot shows, from the FRESH-AIR ACH: null = the neutral "pending" dot (the air age is not solved yet, or
    /// the room is a loop in open air). Never graded from the total exchange.</summary>
    public static AchBand? Verdict(RoomResult r, AchIntent intent) =>
        r.ExchangeOnly || float.IsNaN(r.FreshAch) ? null : Band(r.FreshAch, intent);

    /// <summary>The thresholds in one line, for the chip legend.</summary>
    public static string Summary(AchIntent intent) => intent == AchIntent.Cooling
        ? $"cooling · good ≥ {CoolingGood:0.#}, weak ≥ {CoolingPoor:0.#} ACH"
        : $"fresh-air ACH from air age · good ≥ {AirQualityGood:0.#}, weak ≥ {AirQualityPoor:0.##}";

    /// <summary>The full tooltip: what the dot means, the numbers, and where each one comes from. Shown on
    /// every room row, because a threshold with no citation is just a colour.</summary>
    public static string Tooltip(AchIntent intent) => intent == AchIntent.Cooling
        ? "Dot: fresh-air ACH (air changes per hour from the age of air), judged as COOLING.\n" +
          $"Green ≥ {CoolingGood:0.#} ACH · amber ≥ {CoolingPoor:0.#} · red below.\n" +
          CoolingSource + "\n\n" +
          "The tag, if any, is one comfort word (stuffy, draughty, uneven); hover it for the reason."
        : "Dot: fresh-air ACH (air changes per hour from the age of air), judged as AIR QUALITY.\n" +
          "It is outdoor air only: air that comes through other rooms counts at its real age, so a room fed by a neighbour reads low.\n" +
          $"Green ≥ {AirQualityGood:0.#} ACH · amber ≥ {AirQualityPoor:0.##} · red below.\n" +
          AirQualityGoodSource + "\n" +
          AirQualityPoorSource + "\n\n" +
          "The tag, if any, is one comfort word (stuffy, draughty, uneven); hover it for the reason.";

    // ---------------------------------------------------------------- comfort flag

    /// <summary>QUOTED. ASHRAE Standard 55 (elevated air speed, section 5.3.3 in the 2017/2020 editions): above
    /// 0.8 m/s (150 fpm) average air speed the occupants must have local control of it. The flag uses the room's
    /// occupied-zone mean speed as that average; it does not account for temperature (a warm room may want it).</summary>
    public const float DraughtSpeed = 0.8f;

    /// <summary>Share of the occupied zone below the still-air threshold above which a well-ventilated room is
    /// "uneven" (the existing tag; a rule of thumb, stated in the tooltip).</summary>
    public const float UnevenStill = 0.4f;

    /// <summary>One comfort word per room, or none. In order: an open-air loop has no ACH to judge; "stuffy" is the
    /// red band of the chosen question; "draughty" is the ASHRAE 55 speed; "uneven" is plenty of air that misses
    /// part of the room. Nothing here is a comfort model (no PMV): that needs a thermal comfort run.</summary>
    public static (string Flag, string Tip) ComfortFlag(RoomResult r, float still, AchIntent intent)
    {
        if (ExchangeOnly(r)) return ("open air", ExchangeTip);
        bool known = !float.IsNaN(r.FreshAch);   // no age yet: no verdict (and never a total-exchange fallback)
        var band = known ? Band(r.FreshAch, intent) : AchBand.Weak;
        if (known && band == AchBand.Poor)
            return ("stuffy", intent == AchIntent.Cooling
                ? $"Under {CoolingPoor:0.#} fresh-air ACH: too little outdoor air to carry heat out. {CoolingSource}"
                : $"Under {AirQualityPoor:0.##} fresh-air ACH: too little outdoor air reaches this room for the people in it. {AirQualityPoorSource}");
        if (r.MeanSpeed > DraughtSpeed)
            return ("draughty", $"The time-mean air speed, averaged over the occupied zone, is {r.MeanSpeed:0.00} m/s (local jets are faster). ASHRAE 55 asks for occupant control " +
                                $"of air speed above {DraughtSpeed:0.0} m/s (150 fpm); a warm room may welcome it, a cool one will not.");
        if (known && still > UnevenStill && band == AchBand.Good)
            return ("uneven", $"Plenty of air overall, but {still * 100:0} % of the occupied zone is below the still-air " +
                              "threshold, so the air is not reaching the whole room. (Rule of thumb: more than 40 %.)");
        return ("", "");
    }

    // ---------------------------------------------------------------- exchange, not ACH

    /// <summary>A room whose outline sits in open air (typically a detected room on an imported mesh). Core
    /// counts as inflow only air arriving through an opening, a device or the domain boundary, and reports what
    /// crosses the outline through plain air separately (RoomResult.InternalExchange). With next to no real
    /// inflow the exchange is recirculation round a loop drawn in open air, not a room's ventilation, so the row
    /// shows the exchange instead. Next to no inflow means under 5 % of the exchange, the mirror of the 5 % above
    /// which Core adds its own note.</summary>
    public static bool ExchangeOnly(RoomResult r) => r.ExchangeOnly;

    /// <summary>The room value column: fresh-air ACH ("—" until the age is solved, "sealed" at the age cap), or the exchange for a
    /// room loop in open air.</summary>
    public static string RoomValue(RoomResult r) => ExchangeOnly(r) ? $"exchange {r.InternalExchange:0.0#} m³/s" : FreshText(r);

    /// <summary>The fresh-air number alone: "—" while the age is missing, "sealed" when no air path reaches the room.</summary>
    public static string FreshText(RoomResult r) =>
        float.IsNaN(r.FreshAch) ? "—" : r.AgeSealed ? "sealed" : $"{r.FreshAch:0.0}";

    /// <summary>The same with its unit, for one-line summaries (status strip, bench).</summary>
    public static string AchText(RoomResult r) => ExchangeOnly(r) ? RoomValue(r)
        : float.IsNaN(r.FreshAch) || r.AgeSealed ? FreshText(r) : $"{r.FreshAch:0.0} fresh-air ACH";

    /// <summary>The total air exchange with its definition, for Expert lines and tooltips.</summary>
    public static string TotalText(RoomResult r) => $"total air exchange {r.Ach:0.0} ACH (incl. air from other rooms)";

    public const string ExchangeTip =
        "No ACH for this room: its outline runs through open air rather than walls, so almost nothing enters " +
        "it through an opening or a device. The number is the air crossing the outline through plain air " +
        "(m³/s), which is mostly the same air circulating, not fresh air, so it is not an air change rate. " +
        "Typical of a room detected on an imported mesh with no walls at that edge.";
}
