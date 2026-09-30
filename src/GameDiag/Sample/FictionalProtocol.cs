using System.Buffers.Binary;

namespace GameDiag.Sample;

/// <summary>
/// Protocole fictif utilisé par l'auto-test et documenté dans rules.yaml.
/// </summary>
public static class FictionalProtocol
{
    public static byte[] Hello(ushort proto, ushort sequence, byte opcode)
    {
        var message = new byte[8];
        message[0] = 0x48;
        message[1] = 0x49;
        BinaryPrimitives.WriteUInt16LittleEndian(message.AsSpan(2), proto);
        BinaryPrimitives.WriteUInt16LittleEndian(message.AsSpan(4), sequence);
        message[6] = opcode;
        message[7] = 0x00;
        return message;
    }

    public static byte[] Tick(ushort sequence, uint itemId, uint price)
    {
        var message = new byte[15];
        message[0] = 0x47;
        message[1] = 0x44;
        BinaryPrimitives.WriteUInt16LittleEndian(message.AsSpan(2), 15);
        message[4] = 0x01;
        BinaryPrimitives.WriteUInt16LittleEndian(message.AsSpan(5), sequence);
        BinaryPrimitives.WriteUInt32LittleEndian(message.AsSpan(7), itemId);
        BinaryPrimitives.WriteUInt32LittleEndian(message.AsSpan(11), price);
        return message;
    }
}
