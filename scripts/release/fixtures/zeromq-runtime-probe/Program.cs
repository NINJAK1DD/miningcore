using ZeroMQ;

using var socket = new ZSocket(ZSocketType.PAIR);
Console.WriteLine("ZeroMQ managed binding loaded its native runtime provider");
