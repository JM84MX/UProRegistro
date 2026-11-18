using System.Globalization;
using UProAttendance.Core;

namespace UProAttendance.Presentation;

internal sealed class AttendanceGreetings
{
    private readonly Dictionary<AttendanceAction, int> previous = [];

    public string NextBreak(AttendanceAction action, string name)
    {
        var options = BreakTemplates(action);
        return string.Format(CultureInfo.CurrentCulture, options[NextIndex(action, options.Count)], name);
    }

    public static string ConfirmationSalutation(AttendanceAction action, string name)
    {
        var trimmed = name.AsSpan().Trim();
        var separator = trimmed.IndexOfAny(" \t\r\n".AsSpan());
        var firstName = (separator < 0 ? trimmed : trimmed[..separator]).ToString();
        return Greet(action, firstName);
    }

    public static string ConfirmationSalutation(AttendanceAction action, Employee employee) =>
        string.IsNullOrWhiteSpace(employee.Alias) ? ConfirmationSalutation(action, employee.Name) : Greet(action, employee.Alias.Trim());

    private static string Greet(AttendanceAction action, string name)
    {
        var greeting = action == AttendanceAction.Entry ? "Qué gusto verte" : "Gracias por hoy";
        return string.IsNullOrEmpty(name) ? $"{greeting}." : $"{greeting}, {name}.";
    }

    private int NextIndex(AttendanceAction action, int count)
    {
        var hasPrevious = previous.TryGetValue(action, out var last);
        var index = Random.Shared.Next(hasPrevious ? count - 1 : count);
        if (hasPrevious && index >= last)
            index++;

        previous[action] = index;
        return index;
    }

    internal static IReadOnlyList<string> BreakTemplates(AttendanceAction action) => action switch
    {
        AttendanceAction.BreakStart => BreakStart,
        AttendanceAction.BreakEnd => BreakEnd,
        _ => throw new ArgumentOutOfRangeException(nameof(action))
    };

    private static readonly string[] BreakStart =
    [
        "Disfruta tu descanso, {0}. Es tu momento.",
        "Buen descanso, {0}. Ojalá puedas comer con calma.",
        "Tómate un respiro, {0}. Nos vemos en un rato.",
        "Que disfrutes la pausa, {0}.",
        "Es hora de descansar, {0}. Haz algo que te siente bien.",
        "Que te caiga rico el descanso, {0}.",
        "Desconecta un ratito, {0}. La silla puede esperar.",
        "Buen descanso, {0}. Siéntate, estírate o simplemente respira.",
        "Una pausa merecida, {0}. Que la disfrutes.",
        "Te toca un rato para ti, {0}.",
        "Descansa las manos un momento, {0}. Se lo han ganado.",
        "Que se te haga agradable la pausa, {0}.",
        "Disfruta lo que se te antoje, {0}. Ahora toca descansar.",
        "Date unos minutos de tranquilidad, {0}.",
        "Buen provecho, {0}, si ya vas a comer.",
        "Aprovecha el descanso a tu manera, {0}.",
        "Que tu pausa tenga algo que te guste, {0}.",
        "Estira los hombros y suelta el día un ratito, {0}.",
        "Si puedes, come algo rico y sin prisa, {0}.",
        "Ojalá esta pausa te deje con ganas de volver, {0}.",
    ];

    private static readonly string[] BreakEnd =
    [
        "¡Qué gusto que vuelvas, {0}! Que vaya bien el resto del día.",
        "¡Bienvenida de vuelta, {0}! Vamos con calma a la siguiente cita.",
        "Ojalá hayas descansado, {0}. Que siga bonito el día.",
        "Ya estás de vuelta, {0}. Ojalá lo que queda del día te traiga una alegría.",
        "¡De vuelta, {0}! Que fluya la próxima cita.",
        "Qué bueno verte otra vez, {0}. A retomar a tu ritmo.",
        "¡Seguimos, {0}! Una cita a la vez.",
        "¡Hola de nuevo, {0}! Que el resto de la jornada se sienta ligero.",
        "Después de la pausa, un nuevo comienzo, {0}.",
        "Qué gusto tenerte de vuelta, {0}. Ojalá disfrutes lo que viene.",
        "Vamos con la siguiente cita, {0}. Confía en tu técnica.",
        "¡Qué bueno que regresaste, {0}! Todavía hay tiempo para un gran look.",
        "¡Bienvenida otra vez, {0}! Que encuentres una idea que te entusiasme.",
        "Seguimos, {0}. Ojalá te encuentres con una conversación bonita.",
        "Qué gusto que estés de vuelta, {0}. Que las cosas fluyan.",
        "Retoma con calma, {0}. Lo que sigue también merece su tiempo.",
        "¡Hola otra vez, {0}! Que termines con un look que te deje contenta.",
        "De vuelta a la silla, {0}. Que la siguiente cita sea de las buenas.",
        "¡Seguimos, {0}! Que el resto del día tenga espacio para disfrutar.",
        "Que vuelvas con las manos descansadas y la cabeza fresca, {0}.",
    ];
}
