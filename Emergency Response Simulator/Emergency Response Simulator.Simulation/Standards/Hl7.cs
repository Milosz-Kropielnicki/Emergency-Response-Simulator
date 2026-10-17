using System.Globalization;
using System.Text;
using Emergency_Response_Simulator.Core.Contracts;
using Emergency_Response_Simulator.Core.Model;

namespace Emergency_Response_Simulator.Simulation.Standards;

/// <summary>One HL7 v2 message: its segments, each split into fields.</summary>
public sealed class Hl7Message
{
    private readonly List<string[]> _segments;

    private Hl7Message(List<string[]> segments) => _segments = segments;

    public IReadOnlyList<string[]> Segments => _segments;

    /// <summary>"ADT^A04": the message type and trigger event.</summary>
    public string Type => $"{Get("MSH", 9, 1)}^{Get("MSH", 9, 2)}";

    public string Event => Get("MSH", 9, 2);
    public string ControlId => Get("MSH", 10);

    /// <summary>
    /// A field (1-based, as the standard numbers them) or one component of it, unescaped; "" when absent. MSH counts its
    /// field separator as MSH-1, so MSH-n is the (n-1)th piece after the segment name.
    /// </summary>
    public string Get(string segment, int field, int component = 0, int occurrence = 1)
    {
        var fields = _segments.Where(s => s[0] == segment).Skip(occurrence - 1).FirstOrDefault();
        var index = segment == "MSH" ? field - 1 : field;
        if (fields is null || index < 1 || index >= fields.Length) return "";
        var value = fields[index];
        if (component > 0)
        {
            var parts = value.Split('^');
            value = component <= parts.Length ? parts[component - 1] : "";
        }
        return Hl7.Unescape(value);
    }

    public static Hl7Message Parse(string text)
    {
        var segments = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line => line.Length >= 3)
            .Select(line => line.Split('|'))
            .ToList();
        if (segments.Count == 0 || segments[0][0] != "MSH") throw new FormatException("An HL7 v2 message starts with an MSH segment.");
        return new Hl7Message(segments);
    }

    /// <summary>Every message in a batch: each starts at an MSH (batch headers FHS/BHS/BTS/FTS are skipped).</summary>
    public static IReadOnlyList<Hl7Message> ParseBatch(string text)
    {
        var messages = new List<Hl7Message>();
        var current = new StringBuilder();
        foreach (var line in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()))
        {
            if (line.StartsWith("FHS", StringComparison.Ordinal) || line.StartsWith("BHS", StringComparison.Ordinal)
                || line.StartsWith("BTS", StringComparison.Ordinal) || line.StartsWith("FTS", StringComparison.Ordinal)) continue;
            if (line.StartsWith("MSH", StringComparison.Ordinal) && current.Length > 0)
            {
                messages.Add(Parse(current.ToString()));
                current.Clear();
            }
            current.Append(line).Append('\r');
        }
        if (current.Length > 0) messages.Add(Parse(current.ToString()));
        if (messages.Count == 0) throw new FormatException("No HL7 v2 message (no MSH segment) found.");
        return messages;
    }
}

/// <summary>
/// HL7 v2.5.1 for hospital data (Phase 20). Out: an ADT message to each receiving hospital for each patient on the tracking
/// board who is going to or is at it: A05 (pre-admit: an ambulance is bringing them), A04 (registered at the emergency
/// department), A02 (transferred on) and A03 with disposition 20 (died). Every message is marked processing id T
/// (training). In: hospitals' ADT messages update the board, and each gets an ACK.
/// </summary>
public static class Hl7
{
    public const string Version = "2.5.1";

    /// <summary>MSH-11: T is training, so no live hospital system treats these as real patients.</summary>
    public const string ProcessingId = "T";

    /// <summary>PV1-36 discharge disposition "expired".</summary>
    public const string Expired = "20";

    public const string Separator = "\r";

    public static string Escape(string? text) => (text ?? "")
        .Replace("\\", "\\E\\").Replace("|", "\\F\\").Replace("^", "\\S\\").Replace("&", "\\T\\").Replace("~", "\\R\\")
        .Replace("\r", " ").Replace("\n", " ");

    public static string Unescape(string text) => text
        .Replace("\\F\\", "|").Replace("\\S\\", "^").Replace("\\T\\", "&").Replace("\\R\\", "~").Replace("\\E\\", "\\");

    public static string Time(DateTimeOffset at) => at.UtcDateTime.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);

    public static string TriageCode(Triage triage) => triage switch
    {
        Triage.Immediate => "P1^Immediate^ERS",
        Triage.Urgent => "P2^Urgent^ERS",
        Triage.Delayed => "P3^Delayed^ERS",
        _ => "DEAD^Dead^ERS",
    };

    public static Triage? TriageOf(string code) => code.Trim().ToUpperInvariant() switch
    {
        "P1" or "IMMEDIATE" or "T1" or "RED" => Triage.Immediate,
        "P2" or "URGENT" or "T2" or "YELLOW" => Triage.Urgent,
        "P3" or "DELAYED" or "T3" or "GREEN" => Triage.Delayed,
        "DEAD" or "DECEASED" or "BLACK" => Triage.Deceased,
        _ => null,
    };

    /// <summary>The ADT messages for the patients command is sending to (or has sent to) hospitals, oldest first.</summary>
    public static IReadOnlyList<string> FromCop(ICopService cop, string sendingFacility, Guid? incidentId = null)
    {
        var messages = new List<string>();
        foreach (var patient in cop.Health.Patients.Where(p => p.HospitalId is not null && (incidentId is null || p.IncidentId == incidentId)).OrderBy(p => p.TaggedAt))
        {
            var hospital = cop.Hospitals.FirstOrDefault(h => h.Id == patient.HospitalId);
            if (hospital is null) continue;
            var (trigger, at) = patient.State switch
            {
                CasualtyState.Transporting => ("A05", patient.LeftSceneAt ?? patient.TaggedAt),
                CasualtyState.AtHospital when patient.Transfers > 0 => ("A02", patient.ArrivedAt ?? patient.TaggedAt),
                CasualtyState.AtHospital => ("A04", patient.ArrivedAt ?? patient.TaggedAt),
                CasualtyState.Deceased => ("A03", patient.DiedAt ?? patient.TaggedAt),
                _ => ("", default(DateTimeOffset)),
            };
            if (trigger == "") continue;
            messages.Add(Adt(trigger, at, sendingFacility, hospital.Name, patient));
        }
        return messages;
    }

    public static string Adt(string trigger, DateTimeOffset at, string sendingFacility, string hospital, PatientRecord patient)
    {
        var structure = trigger switch { "A05" => "ADT_A05", "A02" => "ADT_A02", "A03" => "ADT_A03", _ => "ADT_A01" };
        var pv1 = new string[45];
        Array.Fill(pv1, "");
        pv1[0] = "PV1";
        pv1[1] = "1";
        pv1[2] = "E";                                                        // patient class: emergency
        pv1[3] = $"ED^^^{Escape(hospital)}";                                 // assigned location: the emergency department
        pv1[4] = "E";                                                        // admission type: emergency
        pv1[19] = Escape(patient.Tag);                                       // visit number: the triage tag
        if (trigger == "A03") pv1[36] = Expired;
        pv1[44] = Time(patient.ArrivedAt ?? at);

        var segments = new List<string>
        {
            $"MSH|^~\\&|ERS|{Escape(sendingFacility)}|HIS|{Escape(hospital)}|{Time(at)}||ADT^{trigger}^{structure}|ERS{patient.Id.ToString("N")[..12].ToUpperInvariant()}{trigger}|{ProcessingId}|{Version}",
            $"EVN|{trigger}|{Time(at)}",
            $"PID|1||{Escape(patient.Tag)}^^^ERS^PI||UNKNOWN^{Escape(patient.Tag)}^^^^^U",
            string.Join('|', pv1).TrimEnd('|'),
            $"OBX|1|CWE|ERS-TRIAGE^Major incident triage category^L||{TriageCode(patient.Triage)}||||||F",
            $"DG1|1||^{Escape(patient.Injury)}^L|||W",
        };
        return string.Join(Separator, segments) + Separator;
    }

    /// <summary>The acknowledgement for a message: AA (accepted) or AE (error, with the reason).</summary>
    public static string Ack(Hl7Message message, bool accepted, string? text, DateTimeOffset at) =>
        string.Join(Separator,
            $"MSH|^~\\&|ERS|ERS|{Escape(message.Get("MSH", 3))}|{Escape(message.Get("MSH", 4))}|{Time(at)}||ACK^{message.Event}^ACK|ACK{message.ControlId}|{ProcessingId}|{Version}",
            $"MSA|{(accepted ? "AA" : "AE")}|{Escape(message.ControlId)}" + (text is null ? "" : $"|{Escape(text)}")) + Separator;
}
