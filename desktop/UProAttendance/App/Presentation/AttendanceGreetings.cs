using UProAttendance.Core;

namespace UProAttendance.Presentation;

internal sealed class AttendanceGreetings
{
    private readonly Dictionary<AttendanceAction, int> previous = [];

    public string Next(AttendanceAction action, string name)
    {
        var options = Templates(action);
        var index = Random.Shared.Next(options.Count - (previous.ContainsKey(action) ? 1 : 0));
        if (previous.TryGetValue(action, out var last) && index >= last)
            index++;

        previous[action] = index;
        return string.Format(System.Globalization.CultureInfo.CurrentCulture, options[index], name);
    }

    internal static IReadOnlyList<string> Templates(AttendanceAction action) => action switch
    {
        AttendanceAction.Entry => Entry,
        AttendanceAction.BreakStart => BreakStart,
        AttendanceAction.BreakEnd => BreakEnd,
        AttendanceAction.Exit => Exit,
        _ => throw new ArgumentOutOfRangeException(nameof(action))
    };

    private static readonly string[] Entry =
    [
        "¡Hola, {0}! Que tengas un gran día.",
        "Qué gusto verte, {0}. Hoy puedes alegrarle el día a alguien.",
        "¡Bienvenida, {0}! Que hoy te sobren ideas y buenos momentos.",
        "Hola, {0}. Hoy alguien saldrá de tu silla sintiéndose un poquito más como quiere.",
        "¡Qué bueno que llegaste, {0}! Que sea un día de buenos cambios.",
        "¡Que arranque bien el día, {0}! Confía en tu ojo y en tus manos.",
        "¡Buen día, {0}! Ojalá hoy te toque un cambio de look de esos que se recuerdan.",
        "Hola, {0}. Una buena consulta puede ser el comienzo de algo bonito.",
        "¡Bienvenida, {0}! Que cada cita te dé un motivo para sonreír.",
        "¡Hola, {0}! Que hoy haya tiempo para crear, escuchar y disfrutar.",
        "Buen día, {0}. Tu manera de cuidar los detalles se nota.",
        "¡Qué gusto verte, {0}! Hoy puedes sorprenderte con lo que haces.",
        "Hola, {0}. Ojalá disfrutes tanto el proceso como el resultado.",
        "¡Bienvenida, {0}! Empieza con calma y haz espacio para las buenas ideas.",
        "Hola, {0}. Hoy es un buen día para probar algo nuevo.",
        "¡Buen día, {0}! A veces, el mejor cambio empieza escuchando.",
        "¡Hola, {0}! Que hoy salgan cortes bonitos y conversaciones que se disfrutan.",
        "Hola, {0}. Que te acompañen la paciencia, la creatividad y un buen café.",
        "¡Qué gusto que estés aquí, {0}! Que disfrutes lo que vas a crear.",
        "¡Bienvenida, {0}! Que al final del día tengas algo bonito que contar.",
    ];

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

    private static readonly string[] Exit =
    [
        "¡Gracias por hoy, {0}! Que descanses.",
        "¡Hasta luego, {0}! Que llegues bien a casa.",
        "Buen trabajo hoy, {0}. Ahora disfruta lo que queda de tu día.",
        "Nos vemos, {0}. Ojalá tengas una noche tranquila.",
        "¡Hasta la próxima, {0}! Gracias por el cuidado de cada cita.",
        "Que tengas un buen camino de vuelta, {0}.",
        "Gracias por el día, {0}. Disfruta el tiempo para ti.",
        "¡Que descanses, {0}! Deja las tijeras y suelta los hombros.",
        "Buen cierre de jornada, {0}. Que te espere algo bonito.",
        "Hasta luego, {0}. Seguro hubo algún momento que valió la pena.",
        "¡Nos vemos pronto, {0}! Que termines bien el día.",
        "Gracias por ponerle tu toque al día, {0}.",
        "Que tengas una vuelta a casa tranquila, {0}.",
        "Buen descanso, {0}. Mañana la silla vuelve a estar lista.",
        "Ya acabaste por hoy, {0}. Disfruta tu rato libre.",
        "Que te lleves lo mejor del día, {0}. ¡Hasta pronto!",
        "Gracias por la paciencia y los detalles de hoy, {0}.",
        "Hasta la próxima, {0}. Ojalá puedas hacer algo que te guste.",
        "Que la noche te dé tiempo para descansar, {0}.",
        "Gracias por estar hoy, {0}. Cuídate y que llegues bien.",
    ];
}
