namespace Overwatch.SelfTest;

public static class SampleRules
{
    public const string Yaml = """
        version: 1
        rules:
          - id: session_hello
            description: Poignée de main fictive
            direction: c2s
            min_length: 8
            max_length: 8
            header_hex: "4849"
            context:
              sets: [session]
            extract:
              - name: proto
                offset: 2
                type: uint16
              - name: seq
                offset: 4
                type: uint16
              - name: opcode
                offset: 6
                type: uint8

          - id: market_tick
            description: Cote fictive
            direction: s2c
            min_length: 15
            max_length: 4096
            header_hex: "47 44"
            length_field:
              offset: 2
              size: 2
              endian: little
              bias: 0
            context:
              requires: [session]
            extract:
              - name: opcode
                offset: 4
                type: uint8
              - name: seq
                offset: 5
                type: uint16
              - name: item_id
                offset: 7
                type: uint32
              - name: price
                offset: 11
                type: uint32
        """;
}
