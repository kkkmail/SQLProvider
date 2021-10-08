module FSharp.Data.Sql.Ssdt.DacpacParser

open System
open System.Xml
open System.IO.Compression

type SsdtSchema = {
    Tables: SsdtTable list
    TryGetTableByName: string -> SsdtTable option
    StoredProcs: SsdtStoredProc list
    Relationships: SsdtRelationship list
    Descriptions: SsdtDescriptionItem list
}
and SsdtTable = {
    FullName: string
    Schema: string
    Name: string
    Columns: SsdtColumn list
    PrimaryKey: PrimaryKeyConstraint option
    IsView: bool
}
and SsdtColumn = {
    FullName: string
    Name: string
    Description: string
    DataType: string
    AllowNulls: bool
    IsIdentity: bool
    HasDefault: bool
    ComputedColumn: bool
}
and SsdtView = {
    FullName: string
    Schema: string
    Name: string
    Columns: SsdtViewColumn list
    DynamicColumns: SsdtViewColumn list
    Annotations: CommentAnnotation list
}
and SsdtViewColumn = {
    FullName: string
    ColumnRefPath: string option
}
and CommentAnnotation = {
    Column: string
    DataType: string
    Nullability: string option
}
and SsdtRelationship = {
    Name: string
    DefiningTable: RefTable
    ForeignTable: RefTable
}
and RefTable = {
    FullName: string
    Schema: string
    Name: string
    Columns: ConstraintColumn list
}

and PrimaryKeyConstraint = {
    Name: string
    Columns: ConstraintColumn list
}
and ConstraintColumn = {
    FullName: string
    Name: string
}
and SsdtStoredProc = {
    FullName: string
    Schema: string
    Name: string
    Parameters: SsdtStoredProcParam list
}
and SsdtStoredProcParam = {
    FullName: string
    Name: string
    DataType: string
    Length: int option
    IsOutput: bool
}
and SsdtDescriptionItem = {
    DecriptionType: string
    Schema: string
    TableName: string
    ColumnName: string option
    Description: string
}

module RegexParsers =
    open System.Text.RegularExpressions

    /// Splits a fully qualified name into parts. 
    /// Name can start with a letter, _, @ or #. Names in square brackets can contain any char except for square brackets.
    let splitFullName (fn: string) =
        Regex.Matches(fn, @"(\[(?<Brackets>[A-Za-z_@#]+[^\[\]]*)\]|(?<NoBrackets>[A-Za-z_@#]+[A-Za-z09_]*)(\.)?)", RegexOptions.IgnoreCase)
        |> Seq.cast<Match>
        |> Seq.collect(fun m ->
            seq { yield! m.Groups.["Brackets"].Captures |> Seq.cast<Capture>
                  yield! m.Groups.["NoBrackets"].Captures |> Seq.cast<Capture> }
        )
        |> Seq.map (fun c -> c.Value)
        |> Seq.toArray

    /// Tries to find an in-line commented type annotation in a computed table column.
    let parseTableColumnAnnotation colName colExpression =
        let m = Regex.Match(colExpression, @"\/\*\s*(?<DataType>\w*)\s*(?<Nullability>(null|not null))?\s*\*\/", RegexOptions.IgnoreCase)
        if m.Success then
            Some { Column = colName
                   DataType = m.Groups.["DataType"].Captures.[0].Value
                   Nullability = m.Groups.["Nullability"].Captures |> Seq.cast<Capture> |> Seq.toList |> List.tryHead |> Option.map (fun c -> c.Value) }
        else None

    /// Tries to find in-line commented type annotations in a view declaration.
    let parseViewAnnotations sql =
        Regex.Matches(sql, @"\[?(?<Column>\w+)\]?\s*\/\*\s*(?<DataType>\w*)\s*(?<Nullability>(null|not null))?\s*\*\/", RegexOptions.IgnoreCase)
        |> Seq.cast<Match>
        |> Seq.map (fun m ->
            { Column = m.Groups.["Column"].Captures.[0].Value
              DataType = m.Groups.["DataType"].Captures.[0].Value
              Nullability = m.Groups.["Nullability"].Captures |> Seq.cast<Capture> |> Seq.toList |> List.tryHead |> Option.map (fun c -> c.Value) }
        )
        |> Seq.toList
    
/// Extracts model.xml from the given .dacpac file path.
let extractModelXml (dacPacPath: string) = 
    use stream = new IO.FileStream(dacPacPath, IO.FileMode.Open, IO.FileAccess.Read)
    use zip = new ZipArchive(stream, ZipArchiveMode.Read, false)
    let modelEntry = zip.GetEntry("model.xml")
    use modelStream = modelEntry.Open()
    use rdr = new IO.StreamReader(modelStream)
    rdr.ReadToEnd()

/// Returns a doc and node/nodes ns helper fns
let toXmlNamespaceDoc ns xml =
    let doc = new XmlDocument()
    let nsMgr = XmlNamespaceManager(doc.NameTable)
    nsMgr.AddNamespace("x", ns)
    doc.LoadXml(xml)

    let node (path: string) (node: XmlNode) =
        node.SelectSingleNode(path, nsMgr)

    let nodes (path: string) (node: XmlNode) =
        node.SelectNodes(path, nsMgr) |> Seq.cast<XmlNode>
            
    doc, node, nodes    

let attMaybe (nm: string) (node: XmlNode) = 
    node.Attributes 
    |> Seq.cast<XmlAttribute> 
    |> Seq.tryFind (fun a -> a.Name = nm) 
    |> Option.map (fun a -> a.Value) 

let att (nm: string) (node: XmlNode) = 
    attMaybe nm node |> Option.defaultValue ""

/// Parses the xml that is extracted from a .dacpac file.
let parseXml(xml: string) =
    let removeBrackets (s: string) = s.Replace("[", "").Replace("]", "")

    let doc, node, nodes = xml |> toXmlNamespaceDoc "http://schemas.microsoft.com/sqlserver/dac/Serialization/2012/02"
    let model = doc :> XmlNode |> node "/x:DataSchemaModel/x:Model"

    let parsePrimaryKeyConstraint (pkElement: XmlNode) =
        let name = pkElement |> att "Name"
        let relationship = pkElement |> nodes "x:Relationship" |> Seq.find (fun r -> r |> att "Name" = "ColumnSpecifications")
        let columns =
            relationship
            |> nodes "x:Entry"
            |> Seq.map (node "x:Element/x:Relationship/x:Entry/x:References" >> att "Name")
            |> Seq.map (fun fnm -> { ConstraintColumn.FullName = fnm; Name = fnm |> RegexParsers.splitFullName |> Array.last })
            |> Seq.toList
        { PrimaryKeyConstraint.Name = name
          PrimaryKeyConstraint.Columns = columns }

    let pkConstraintsByColumn =
        model
        |> nodes "x:Element"
        |> Seq.filter (fun e -> e |> att "Type" = "SqlPrimaryKeyConstraint")
        |> Seq.map parsePrimaryKeyConstraint
        |> Seq.collect (fun pk -> pk.Columns |> List.map (fun col -> col, pk))
        |> Seq.toList

    let parseFkRelationship (fkElement: XmlNode) =
        let name = fkElement |> att "Name"
        let localColumns = fkElement |> nodes "x:Relationship" |> Seq.find(fun r -> r |> att "Name" = "Columns") |> nodes "x:Entry/x:References" |> Seq.map (att "Name")
        let localTable = fkElement |> nodes "x:Relationship" |> Seq.find(fun r -> r |> att "Name" = "DefiningTable") |> node "x:Entry/x:References" |> att "Name"
        let foreignColumns = fkElement |> nodes "x:Relationship" |> Seq.find(fun r -> r |> att "Name" = "ForeignColumns") |> nodes "x:Entry/x:References" |> Seq.map (att "Name")
        let foreignTable = fkElement |> nodes "x:Relationship" |> Seq.find(fun r -> r |> att "Name" = "ForeignTable") |> node "x:Entry/x:References" |> att "Name"
        { SsdtRelationship.Name = name
          SsdtRelationship.DefiningTable =
            let parts = localTable |> RegexParsers.splitFullName
            { RefTable.FullName = localTable
              RefTable.Schema = match parts with | [|schema;name|] -> schema | _ -> ""
              RefTable.Name = match parts with | [|schema;name|] -> name | _ -> "" 
              RefTable.Columns = 
                localColumns
                |> Seq.map (fun fnm -> { ConstraintColumn.FullName = fnm; Name = fnm |> RegexParsers.splitFullName |> Array.last })
                |> Seq.toList }
          SsdtRelationship.ForeignTable =
            let parts = foreignTable |> RegexParsers.splitFullName
            { RefTable.FullName = foreignTable
              RefTable.Schema = match parts with | [|schema;name|] -> schema | _ -> ""
              RefTable.Name = match parts with | [|schema;name|] -> name | _ -> "" 
              RefTable.Columns =
                foreignColumns
                |> Seq.map (fun fnm -> { ConstraintColumn.FullName = fnm; Name = fnm |> RegexParsers.splitFullName |> Array.last })
                |> Seq.toList }
        }

    let relationships =
        model
        |> nodes "x:Element"
        |> Seq.filter (fun e -> e |> att "Type" = "SqlForeignKeyConstraint")
        |> Seq.map parseFkRelationship
        |> Seq.toList
        
    let parseTableColumn (colEntry: XmlNode) =
        let el = colEntry |> node "x:Element"
        let colType, fullName = el |> att "Type", el |> att "Name"
        let colName = fullName |> RegexParsers.splitFullName |> Array.last
        match colType with
        | "SqlSimpleColumn" -> 
            let allowNulls = el |> nodes "x:Property" |> Seq.tryFind (fun p -> p |> att "Name" = "IsNullable") |> Option.map (fun p -> p |> att "Value")
            let isIdentity = el |> nodes "x:Property" |> Seq.tryFind (fun p -> p |> att "Name" = "IsIdentity") |> Option.map (fun p -> p |> att "Value")
            let dataType = el |> node "x:Relationship/x:Entry/x:Element/x:Relationship/x:Entry/x:References" |> att "Name"
            Some
                { SsdtColumn.Name = colName
                  SsdtColumn.FullName = fullName
                  SsdtColumn.AllowNulls = match allowNulls with | Some allowNulls -> allowNulls = "True" | _ -> true
                  SsdtColumn.DataType = dataType |> removeBrackets
                  SsdtColumn.HasDefault = false
                  SsdtColumn.Description = "Simple Column"
                  SsdtColumn.IsIdentity = isIdentity |> Option.map (fun isId -> isId = "True") |> Option.defaultValue false
                  SsdtColumn.ComputedColumn = false}
        | "SqlComputedColumn" ->
            // Check for annotation
            let colExpr = (el |> node "x:Property/x:Value").InnerText
            let annotation = RegexParsers.parseTableColumnAnnotation colName colExpr
            let dataType =
                annotation
                |> Option.map (fun a -> a.DataType.ToUpper()) // Ucase to match typeMappings
                |> Option.defaultValue "SQL_VARIANT"
            let allowNulls =
                match annotation with
                | Some { Nullability = Some nlb } -> nlb.ToUpper() = "NULL"
                | Some { Nullability = None } -> true // Sql Server column declarations allow nulls by default
                | None -> false // Default to "SQL_VARIANT" (obj) with no nulls if annotation is not found
            
            Some
                { SsdtColumn.Name = colName
                  SsdtColumn.FullName = fullName
                  SsdtColumn.AllowNulls = allowNulls
                  SsdtColumn.DataType = dataType
                  SsdtColumn.HasDefault = false
                  SsdtColumn.Description =
                    "Computed Column" +
                        if annotation.IsNone && dataType = "SQL_VARIANT"
                        then ". You can add type annotation to definition SQL to get type. E.g. " + colName + " AS ('c' /* varchar not null */)"
                        else ""
                  SsdtColumn.IsIdentity = false
                  SsdtColumn.ComputedColumn = true}
        | _ ->
            None // Unsupported column type

    let parseTable (tblElement: XmlNode) =
        let fullName = tblElement |> att "Name"
        let relationship = tblElement |> nodes "x:Relationship" |> Seq.find (fun r -> r |> att "Name" = "Columns")
        let columns = relationship |> nodes "x:Entry" |> Seq.choose parseTableColumn |> Seq.toList
        let nameParts = fullName |> RegexParsers.splitFullName
        let primaryKey =
            columns
            |> List.choose(fun c -> pkConstraintsByColumn |> List.tryFind(fun (colRef, pk) -> colRef.FullName = c.FullName))
            |> List.tryHead
            |> Option.map snd
        { Schema = match nameParts with | [|schema;name|] -> schema | _ -> failwithf "Unable to parse table '%s' schema." fullName
          Name = match nameParts with | [|schema;name|] -> name | [|name|] -> name | _ -> failwithf "Unable to parse table '%s' name." fullName
          FullName = fullName
          Columns = columns
          IsView = false
          PrimaryKey = primaryKey } : SsdtTable

    let parseViewColumn (colEntry:  XmlNode) =
        let colFullNm = colEntry |> node "x:Element" |> att "Name"
        let typeRelation = colEntry |> node "x:Element" |> node "x:Relationship" |> Option.ofObj
        let colRefPath = typeRelation |> Option.map (node "x:Entry/x:References" >> att "Name")
        { SsdtViewColumn.FullName = colFullNm
          SsdtViewColumn.ColumnRefPath = colRefPath }

    /// Recursively collections view column refs from any nested 'DynamicObjects' (ex: CTEs).
    let collectDynamicColumnRefs (viewElement: XmlNode) =
        let rec recurse (columns: SsdtViewColumn list) (el: XmlNode)  =
            let relationshipColumns = el |> nodes "x:Relationship" |> Seq.tryFind (fun r -> r |> att "Name" = "Columns")
            let relationshipDynamicObjects = el |> nodes "x:Relationship" |> Seq.tryFind (fun r -> r |> att "Name" = "DynamicObjects")
            let cols = relationshipColumns |> Option.map (nodes "x:Entry" >> Seq.map parseViewColumn) |> Option.defaultValue Seq.empty |> Seq.toList
            let accumulatedColumns = columns @ cols
            match relationshipDynamicObjects with
            | Some rel -> rel |> nodes "x:Entry" |> Seq.map (node "x:Element") |> Seq.collect (recurse accumulatedColumns) |> Seq.toList
            | None -> accumulatedColumns

        recurse [] viewElement

    let parseView (viewElement: XmlNode) =
        let fullName = viewElement |> att "Name"
        let relationshipColumns = viewElement |> nodes "x:Relationship" |> Seq.find (fun r -> r |> att "Name" = "Columns")
        let columns = relationshipColumns |> nodes "x:Entry" |> Seq.map parseViewColumn
        let dynamicColumns = collectDynamicColumnRefs viewElement
        let query = (viewElement |> nodes "x:Property" |> Seq.find (fun n -> n |> att "Name" = "QueryScript") |> node "x:Value").InnerText
        let annotations = RegexParsers.parseViewAnnotations query

        let nameParts = fullName |> RegexParsers.splitFullName
        { FullName = fullName
          Schema = match nameParts with | [|schema;name|] -> schema | _ -> failwithf "Unable to parse view '%s' schema." fullName
          Name = match nameParts with | [|schema;name|] -> name | _ -> failwithf "Unable to parse view '%s' name." fullName
          Columns = columns |> Seq.toList
          DynamicColumns = dynamicColumns
          Annotations = annotations } : SsdtView

    /// Recursively resolves column references.
    let resolveColumnRefPath (tableColumnsByPath: Map<string, SsdtColumn>) (viewColumnsByPath: Map<string, SsdtViewColumn>) (viewCol: SsdtViewColumn) =
        let rec resolve (path: string) =
            match tableColumnsByPath.TryFind(path) with
            | Some tblCol ->
                { tblCol with
                    FullName = viewCol.FullName
                    Name = viewCol.FullName |> RegexParsers.splitFullName |> Array.last } |> Some
            | None -> 
                match viewColumnsByPath.TryFind(path) with
                | Some viewCol when viewCol.ColumnRefPath <> Some path ->
                    match viewCol.ColumnRefPath with
                    | Some colRefPath -> resolve colRefPath
                    | None -> None
                | _ -> None

        match viewCol.ColumnRefPath with
        | Some path -> resolve path
        | None -> None

    let parseStoredProc (spElement: XmlNode) =
        let fullName = spElement |> att "Name"        
        let parameters =
            match spElement |> nodes "x:Relationship" |> Seq.tryFind (fun r -> r |> att "Name" = "Parameters") with
            | Some relationshipParameters ->
                relationshipParameters
                |> nodes "x:Entry"
                |> Seq.map (fun entry ->
                    let el = entry |> node "x:Element"
                    let pFullName = el |> att "Name"
                    let isOutput =
                        match el |> nodes "x:Property" |> Seq.tryFind (fun p -> p |> att "Name" = "IsOutput") with
                        | Some p when p |> att "Value" = "True" -> true
                        | _ -> false

                    let dataType = el |> node "x:Relationship/x:Entry/x:Element/x:Relationship/x:Entry/x:References" |> att "Name"
                    { FullName = pFullName
                      Name = pFullName |> RegexParsers.splitFullName |> Array.last
                      DataType = dataType |> removeBrackets
                      Length = None // TODO: Implement
                      IsOutput = isOutput }
                )
            | None -> Seq.empty

        let parts = fullName |> RegexParsers.splitFullName
        { FullName = fullName
          Schema = parts.[0]
          Name = parts.[1]
          Parameters = parameters |> Seq.toList }

    let parseDescription (extElement: XmlNode) =
        let fullName = extElement |> att "Name"
        let parts = fullName |> RegexParsers.splitFullName
        let description = (extElement |> nodes "x:Property" |> Seq.find (fun n -> n |> att "Name" = "Value") |> node "x:Value").InnerText
        {   // Mostly interesting decription types table/view/column: SqlTableBase / SqlView / SqlColumn
            // But there can be many others too: SqlSchema / SqlDmlTrigger / SqlConstraint / SqlDatabaseOptions / SqlFilegroup / SqlSubroutineParameter / SqlXmlSchemaCollection / ...
            DecriptionType = parts.[0]
            Schema = parts.[1]
            TableName = if parts.Length > 2 then parts.[2] else ""
            ColumnName = if parts.Length > 3 && parts.[0] <> "SqlTableBase" && parts.[3] <> "MS_Description" then Some parts.[3] else None
            Description = description
        }

    let storedProcs =
        model
        |> nodes "x:Element"
        |> Seq.filter (fun e -> e |> att "Type" = "SqlProcedure")
        |> Seq.map parseStoredProc
        |> Seq.toList

    let tables = 
        model
        |> nodes "x:Element"
        |> Seq.filter (fun e -> e |> att "Type" = "SqlTable")
        |> Seq.map parseTable
        |> Seq.toList

    let views =
        model
        |> nodes "x:Element"
        |> Seq.filter (fun e -> e |> att "Type" = "SqlView")
        |> Seq.map parseView
        |> Seq.toList

    let descriptions =
        model
        |> nodes "x:Element"
        |> Seq.filter (fun e -> e |> att "Type" = "SqlExtendedProperty" && (e |> att "Name").EndsWith(".[MS_Description]"))
        |> Seq.map parseDescription
        |> Seq.toList

    let tableColumnsByPath = tables |> List.collect (fun t -> t.Columns) |> List.map (fun c -> c.FullName, c) |> Map.ofList
    let viewColumnsByPath = views |> List.collect (fun v -> v.Columns @ v.DynamicColumns) |> List.map (fun c -> c.FullName, c) |> Map.ofList
    let resolveColumnRefPath = resolveColumnRefPath tableColumnsByPath viewColumnsByPath

    let viewToTable (view: SsdtView) =
        { FullName = view.FullName
          Name = view.Name
          Schema = view.Schema
          IsView = true
          PrimaryKey = None
          Columns =
            view.Columns
            |> List.map (fun vc ->
                let colName = vc.FullName |> RegexParsers.splitFullName |> Array.last
                let annotation = view.Annotations |> List.tryFind (fun a -> a.Column = colName)
                match resolveColumnRefPath vc with
                | Some tc when annotation.IsNone -> tc
                | tcOpt ->
                    // Can't resolve column, or annotation override: try to find a commented type annotation
                    let dataType =
                        annotation
                        |> Option.map (fun a -> a.DataType.ToUpper()) // Ucase to match typeMappings
                        |> Option.defaultValue "SQL_VARIANT"
                    let allowNulls =
                        match annotation with
                        | Some { Nullability = Some nlb } -> nlb.ToUpper() = "NULL"
                        | Some { Nullability = None } -> true // Sql Server column declarations allow nulls by default
                        | None -> false // Default to "SQL_VARIANT" (obj) with no nulls if annotation is not found
                    let description =
                        if dataType = "SQL_VARIANT"
                        then sprintf "Unable to resolve this column's data type from the .dacpac file; consider adding a type annotation in the view. Ex: %s /* varchar not null */ " colName
                        else "This column's data type was resolved from a comment annotation in the SSDT view definition."

                    if dataType = "SQL_VARIANT" && tcOpt.IsSome then tcOpt.Value else
                    { FullName = vc.FullName
                      Name = colName
                      Description = description
                      DataType = dataType
                      AllowNulls = allowNulls
                      HasDefault = false
                      IsIdentity = false
                      ComputedColumn = true } : SsdtColumn
            )
        } : SsdtTable

    let tablesAndViews = tables @ (views |> List.map viewToTable)

    { Tables = tablesAndViews
      StoredProcs = storedProcs
      TryGetTableByName = fun nm -> tablesAndViews |> List.tryFind (fun t -> t.Name = nm)
      Relationships = relationships
      Descriptions = descriptions } : SsdtSchema
