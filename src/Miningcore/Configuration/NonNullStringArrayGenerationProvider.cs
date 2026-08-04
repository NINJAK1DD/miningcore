using Newtonsoft.Json.Schema;
using Newtonsoft.Json.Schema.Generation;

namespace Miningcore.Configuration;

public sealed class NonNullStringArrayGenerationProvider : JSchemaGenerationProvider
{
    public override bool CanGenerateSchema(
        JSchemaTypeGenerationContext context) =>
        context.ObjectType == typeof(string[]);

    public override JSchema GetSchema(JSchemaTypeGenerationContext context)
    {
        var schema = new JSchema
        {
            Type = JSchemaType.Array | JSchemaType.Null,
        };
        schema.Items.Add(new JSchema
        {
            Type = JSchemaType.String,
        });

        return schema;
    }
}
