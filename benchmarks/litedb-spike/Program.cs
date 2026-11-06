// TASK-0110: should harness store its state in LiteDB instead of SQLite? See README.md.
using LiteDbSpike;

return args switch
{
    ["aot"] => AotCheck.Run(),
    ["mutex"] => MutexProbe.Run(),
    ["mutex-child", var name] => MutexProbe.Child(name),
    ["bench", var source, var runs] => Bench.Run(source, int.Parse(runs, System.Globalization.CultureInfo.InvariantCulture)),
    ["contend", var engine, var processes, var writes] => Contend.Run(
        engine,
        int.Parse(processes, System.Globalization.CultureInfo.InvariantCulture),
        int.Parse(writes, System.Globalization.CultureInfo.InvariantCulture)
    ),
    ["contend-child", var engine, var path, var id, var writes] => Contend.Child(
        engine,
        path,
        id,
        int.Parse(writes, System.Globalization.CultureInfo.InvariantCulture)
    ),
    ["hold", var engine] => Contend.Hold(engine, 8),
    ["hold", var engine, var seconds] => Contend.Hold(engine, int.Parse(seconds, System.Globalization.CultureInfo.InvariantCulture)),
    ["hold-child", var engine, var path, var seconds] => Contend.HoldChild(
        engine,
        path,
        int.Parse(seconds, System.Globalization.CultureInfo.InvariantCulture)
    ),
    _ => Usage(),
};

static int Usage()
{
    Console.Error.WriteLine(
        """
        LiteDbSpike mutex                                does a named Mutex (LiteDB shared mode's lock) exclude another process?
        LiteDbSpike aot                                  typed (BsonMapper) and manual mapping under trimming/AOT
        LiteDbSpike bench <harness.db> <runs>            SQLite as harness uses it vs LiteDB on a copy of real data
        LiteDbSpike contend <sqlite|shared|direct> <processes> <writes>
                                                         processes migrating and writing one database at once
        LiteDbSpike hold <sqlite|shared> [seconds]       a reader and a writer waiting on another process's long transaction
        """
    );
    return 2;
}
