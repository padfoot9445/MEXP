using Common.Tokens;
using Common.AST;
using System.Linq.Expressions;
using Common.Logger;
using System.Diagnostics;
namespace Parser;
public partial class Parser
{
    public List<IToken> Input { get; set; } //assume it ends in EOF
    int Current = 0;
    ILogger Log { get; init; }
    private int Position { get => Current; }
    readonly HashSet<TokenType> RecoveryTokens = [TokenType.Semicolon];
    delegate bool ParsingFunction(out AnnotatedNode<Annotations>? Node);
    TypeProvider TP { get; } = new();
    SafeParser SP { get; init; }
    private IToken? CurrentToken(int offset = 0, bool Inc = false)
    {
        if (Current + offset < Input.Count)
        {
            if (Inc)
            {
                return Input[Current++ + offset]; //generalization of Current++ not needed, I think
            }
            return Input[Current + offset];
        }
        return null;
    }
    public Parser(IEnumerable<IToken> tokens, ILogger? logger = null)
    {
        this.Input = tokens.ToList();
        if (Input.Count > 0 && this.Input[^1].TT != TokenType.EOF)
        {
            Input.Add(IToken.NewToken(TokenType.EOF, "EOF", -1));
        }
        Log = logger ?? new Logger();
        SP = new(Log);
    }
    #region InstanceAndStaticParse
    public bool Parse(out AnnotatedNode<Annotations>? node)
    {
        //returns true if parse success; node will be of type AST. If parse failure, returns false; node is undefined
        if (Input.Count == 0)
        {
            node = null;
            return false;
        }
        else if (!SP.SafeParse(Program, out node, Current: ref Current))
        {
            if (Recover())
            {
                Parse(out node);
                //if we skipped forwards, it means that we are parsing from a new location, so we can continue
                //but we still failed so
                return false;
            }
            else
            {
                //no skip forwards, we've discovered as many syntax errors as we can.
                node = null;
                return false;
            }
        }
        return true;
    }

    public static bool Parse(IEnumerable<IToken> Input, out AnnotatedNode<Annotations>? Node, ILogger? Log = null)
    {
        Parser parser;
        if (Log is not null)
        {
            parser = new Parser(Input, Log);
        }
        else
        {
            parser = new Parser(Input);
        }
        return parser.Parse(out Node);
    }
    #endregion
    bool Recover()
    {
        //skip forwards to the next token in RecoveryTokens. If we reach EOF before finding the next token, return false; else true
        while (Input[Current].TT != TokenType.EOF)
        {
            if (RecoveryTokens.Contains(Input[Current++].TT)) //current++ because no matter what we want to focus on the token after the one we are focusing on now
            {
                return true;
            }
        }
        return false;
    }
    #region GenericParsingMethods
    /// <summary>
    /// <Operation> ::= <NextInPriority> <OperationPrime>;
    /// Example:
    /// <Addition> ::= <Multiplication> <AdditionPrime>;
    /// Current Production Name is for naming the AST Node generated
    /// </summary>
    /// <param name="NextInPriority"><NextInPriority></param>
    /// <param name="BinaryPrime"><OperationPrime></param>
    /// <param name="CurrentProductionName"><Operation></param>
    /// <param name="Node">Out</param>
    /// <returns></returns>
    bool PrimedBinary(ParsingFunction NextInPriority, ParsingFunction BinaryPrime, string CurrentProductionName, out AnnotatedNode<Annotations>? Node, Func<int, string>? ErrorMessage = null, Func<ASTNode, AnnotatedNode<Annotations>>? Action = null)
    {
        if (SP.SafeParse(NextInPriority, out AnnotatedNode<Annotations>? Neg, Suppress: false, Current: ref Current) && SP.SafeParse(BinaryPrime, out AnnotatedNode<Annotations>? MulP, Suppress: false, Current: ref Current))
        {
            Node = (Action ?? ((ASTNode x) => new(new(IsEmpty: false), x)))(ASTNode.PrimedBinary(Neg!, MulP!, CurrentProductionName));
            return true;
        }
        Log.Log((ErrorMessage ?? ((x) => $"Error in PrimedBinary at {x}"))(Position));
        Node = null;
        return false;
        //no need for error message here as the explanation of why NextInPriority or BinaryPrime failed to parse will be handled by those methods
    }
    /// <summary>
    /// <OperationPrime> ::= <Operator> <paramref name="NextInPriority"/> <OperationPrime> |
    /// <Operator2> <paramref name="NextInPriority"/> <OperationPrime> | ... |
    /// <Empty>;
    /// Example:
    /// <AdditionPrime> ::=
    ///     "+" <Multiplication> <AdditionPrime> |
    ///     "-" <Multiplication> <AdditionPrime> |
    ///     <Empty>
    ///;
    /// </summary>
    /// <param name="NextInPriority"></param>
    /// <param name="Operators">Set of all valid operators</param>
    /// <param name="CurrentProductionName"></param>
    /// <param name="Node"></param>
    /// <returns></returns>
    bool BinaryPrime(ParsingFunction NextInPriority, ICollection<TokenType> Operators, string CurrentProductionName, out AnnotatedNode<Annotations>? Node, Func<int, string>? MessageOnError, Func<ASTNode, AnnotatedNode<Annotations>>? Action)
    {
        bool Self(out AnnotatedNode<Annotations>? node) => BinaryPrime(NextInPriority, Operators, CurrentProductionName, out node, MessageOnError, Action); //function representing recursive call on self; i.e. the BinaryPrime part of the paths where this is not empty
        if (Operators.Contains(Input[Current].TT))
        {
            IToken Operator = Input[Current];
            Current++;
            if (SP.SafeParse(NextInPriority, out AnnotatedNode<Annotations>? ParentPrimedNode, Suppress: false, Current: ref Current) && SP.SafeParse(Self, out AnnotatedNode<Annotations>? PrimeNode, Suppress: false, Current: ref Current))
            {
                Node = (Action ?? (x => new(new(IsEmpty: false), x)))(ASTNode.BinaryPrime(Operator: Operator, Right: ParentPrimedNode!, Repeat: PrimeNode!, CurrentProductionName));
                return true;
            }
            else
            {
                Log.Log((MessageOnError ?? ((x) => $"Error in BinaryPrime at {x}"))(Position));
                Node = null;
                return false;
                //similarly, faliure to parse ParentPrimedNode or NextInPriority is handled by those methods
            }
        }

        //if not **  must be empty
        Node = (Action ?? (x => new(new(IsEmpty: true), x)))(ASTNode.Empty(CurrentProductionName)); //isempty is true
        return true;
    }
    /// <summary>
    /// 
    /// </summary>
    /// <param name="ErrorMessageFunction">
    /// uint Type1, uint Type2, int Position => string ErrorMessage</param>
    /// <returns></returns>
    /// <exception cref="InvalidOperationException"></exception>
    Func<ASTNode, AnnotatedNode<Annotations>> GetPrimedBinaryAction(Func<uint, uint, int, string> ErrorMessageFunction)
    //Production ::= NextInPriority ProductionPrime;
    {
        return (ASNode) =>
        {
            Debug.Assert(ASNode.Children.Length == 2);
            Annotations NextInPriorityAnno = GetFromChildIndex(ASNode, 0);
            Annotations ProductionPrimeAnno = GetFromChildIndex(ASNode, 1);
            Debug.Assert(NextInPriorityAnno.IsEmpty is false && NextInPriorityAnno.TypeCode is not null);
            if (!ProductionPrimeAnno.IsEmpty)
            {
                Debug.Assert(ProductionPrimeAnno.TypeCode is not null);
                //if this is actually a binary operation

                return new(
                    new(
                        IsEmpty: false,
                        TypeCode: TP.BinOpResultantType((uint)NextInPriorityAnno.TypeCode!, (uint)ProductionPrimeAnno.TypeCode!) ?? throw new InvalidOperationException(ErrorMessageFunction((uint)NextInPriorityAnno.TypeCode!, (uint)ProductionPrimeAnno.TypeCode!, Position))
                    ),
                    ASNode
                );

            }
            else
            {
                //if not, treat it as unary and just return the annotation of the nested type
                Debug.Assert(NextInPriorityAnno.IsEmpty is false);
                return new(
                    NextInPriorityAnno.Copy(),
                    ASNode
                );
            }
        };
    }
    private Func<ASTNode, AnnotatedNode<Annotations>> GetBinaryPrimeAction(Func<uint, uint, int, string> ErrorOnTypeMismatch)
    //BinaryPrime ::= OPERATOR NextInPriority BinaryPrime | Empty
    {
        return (ASNode) =>
        {
            Debug.Assert(ASNode.Children.Length == 3 || ASNode.Children.Length == 0);
            if (ASNode.Children.Length == 0)
            {
                return new(new(IsEmpty: true), ASNode);
            }
            Annotations NextInPriorityAnnotations = GetFromChildIndex(ASNode, 1);
            Annotations BinaryPrimeAnnotations = GetFromChildIndex(ASNode, 2);
            Debug.Assert(NextInPriorityAnnotations.TypeCode is not null);
            if (BinaryPrimeAnnotations.IsEmpty)
            {
                return new(new(TypeCode: NextInPriorityAnnotations.TypeCode), ASNode);
            }
            else
            {
                Debug.Assert(BinaryPrimeAnnotations.TypeCode is not null);
                return new(
                    new(
                        TypeCode: TP.BinOpResultantType((uint)NextInPriorityAnnotations.TypeCode!, (uint)BinaryPrimeAnnotations.TypeCode!) ?? throw new InvalidOperationException(ErrorOnTypeMismatch((uint)NextInPriorityAnnotations.TypeCode, (uint)BinaryPrimeAnnotations.TypeCode, Position))
                    ),
                    ASNode
                );
            }
        };
    }
    #endregion
    private Annotations GetFromChildIndex(ASTNode node, int index)
    {
        if (node.Children.Length <= index)
        {
            throw new ArgumentOutOfRangeException($"Index out of range, {index}, {node.Children.Length}");
        }
        return ((AnnotatedNode<Annotations>)node.Children[index]).Attributes;
    }
    bool Program(out AnnotatedNode<Annotations>? Node)
    {
        if (!SP.SafeParse(Expression, out AnnotatedNode<Annotations>? Expr, Current: ref Current))
        {
            Node = null;
            return false;
        }
        //check if repeat(Semicolon is there); success is not valid in any case
        if (Input[Current].TT == TokenType.Semicolon)
        {
            Current++;
            if (SP.SafeParse(Program, out AnnotatedNode<Annotations>? Repeat, Current: ref Current))
            {
                Node = new(ASTNode.Repeating(Expr!, Repeat!, nameof(Program)));
                return true;
            }
            else
            {
                //check if EOF
                //if no repeat, must be EOF
                if (Input[Current].TT != TokenType.EOF)
                {
                    Log.Log($"Expected EOF at Token Position {Position} but got \"{Input[Current].Lexeme}\"");
                    Node = null;
                    return false;
                }
                IToken OperatorSemicolon = Input[Current];
                Node = new(new(IsEmpty: false), [ASTLeafType.NonTerminal, ASTLeafType.Terminal], [Expr!, OperatorSemicolon], nameof(Program));
                return true;
            }
        }

        else
        {
            Log.Log($"Expected \";\" at {Position}");
            Node = null;
            return false;
        }
    }
    bool Expression(out AnnotatedNode<Annotations>? Node)
    {
        if (SP.SafeParse(Declaration, out AnnotatedNode<Annotations>? Add, Suppress: false, Current: ref Current)) //no additional context to add here so we get the context from safeparse
        {
            Node = new(new(Add!.Attributes.TypeCode, IsEmpty: false), ASTNode.NonTerminal(Add!, nameof(Expression))); //TypeCode <- Addition.TypeCode
            return true;
        }
        Node = null;
        return false;
    }
    bool Declaration(out AnnotatedNode<Annotations>? Node)
    {
        if (SP.SafeParse(Type, out AnnotatedNode<Annotations>? TNode, Current: ref Current))
        {
            IToken IdentToken = CurrentToken(Inc: true)!;
            if (!IdentToken.TCmp(TokenType.Identifier))
            {
                Log.Log($"Expected Identifier after Type at position {Position}");
                Node = null;
                return false;
            }
            if (SP.SafeParse(AssignmentPrime, out AnnotatedNode<Annotations>? ANode, Current: ref Current))
            {
                Debug.Assert(IdentToken.TT == TokenType.Identifier);
                Debug.Assert(TNode!.Attributes.TypeDenotedByIdentifier is not null);
                //verify type safety if AssignmentPrime exists
                if (ANode!.Attributes.IsEmpty is false && !TP.CanBeDeclaredTo(TNode!.Attributes.TypeDenotedByIdentifier!, ANode!.Attributes.TypeCode!))
                {
                    //if there exists an assignmentprime and the declaration is not type-safe then we have an issue; if there does not exist an assignmentprime the declaration DNE so we don't care about types
                    Log.Log($"Type mismatch at position {Position}; Cannot assign {ANode!.Attributes.TypeCode} to {TNode!.Attributes.TypeDenotedByIdentifier}"); //TODO: Reverse typecodes for better error reporting
                    Node = null;
                    return false;
                }
                else
                {
                    TP.StoreIdentifierType(IdentToken.Lexeme, (uint)TNode!.Attributes.TypeDenotedByIdentifier); //store identifier type in type table upon declaration
                    Node = new(new(
                        TypeCode: ANode!.Attributes.IsEmpty is true ? null : TNode!.Attributes.TypeDenotedByIdentifier, //if AssignmentPrime is empty then we cannot give any type to this declaration as an expression
                        IsEmpty: false
                    ), ASTNode.Binary(TNode!, Input[Current - 1], ANode!, nameof(Declaration)));
                    return true;
                }
            }
            else
            {
                Log.Log($"Impossible path in {nameof(Declaration)}");
                Node = null;
                return false;
            }
        }
        else if (SP.SafeParse(Addition, out AnnotatedNode<Annotations>? Add, Suppress: false, Current: ref Current))
        {
            Node = new(Add!.Attributes.Copy(), ASTNode.NonTerminal(Add!, nameof(Declaration)));
            return true;
        }
        Node = null;
        Log.Log($"Expected addition or declaration (Type) at position {Position}");
        return false;
    }

    //<AssignmentPrime> ::= "=" <Addition> <AssignmentPrime> | <Empty>;
    bool AssignmentPrime(out AnnotatedNode<Annotations>? Node)
    => BinaryPrime(Addition, [TokenType.Equals], nameof(AssignmentPrime), out Node, (Pos) => $"Impossible Path in {nameof(AssignmentPrime)}", (ASnode) =>
    {
        if (ASnode.Children.Length == 0) //if assignmentprime is empty
        {
            return new(new(IsEmpty: true), ASnode);
        }
        else
        {
            Debug.Assert(ASnode.Children.Length == 3);
            Debug.Assert(ASnode.Children[2] is AnnotatedNode<Annotations>);
            Annotations NestedAssignmentPrimeAnnotations = GetFromChildIndex(ASnode, 2);
            Annotations AdditionAnnotations = GetFromChildIndex(ASnode, 1);
            if (NestedAssignmentPrimeAnnotations.IsEmpty is true)
            {
                return new(
                    new(IsEmpty: false,
                    TypeCode: AdditionAnnotations.TypeCode
                    ), //= x, TypeCode <- x.TypeCode
                    ASnode
                );
            }
            else
            {
                Debug.Assert(NestedAssignmentPrimeAnnotations.IsEmpty is false);
                if (!AdditionAnnotations.CanBeResolvedToAssignable)
                {
                    throw new InvalidOperationException($"Cannot assign to non-variable at position {Position}");
                    //TODO: Add handling for exceptions in actions in binaryparse methods
                }
                return new(
                    new(TypeCode: NestedAssignmentPrimeAnnotations.TypeCode), // = x (= y), TypeCode <= (= y).TypeCode
                    ASnode
                );
            }
        }
    });

    bool Addition(out AnnotatedNode<Annotations>? Node)
        => PrimedBinary(
            NextInPriority: Multiplication,
            BinaryPrime: AdditionPrime,
            CurrentProductionName: nameof(Addition),
            out Node,
            ErrorMessage: (_) => "", //no error message as we propagate the error down from Multiplication
            Action: GetPrimedBinaryAction((T1, T2, pos) => $"Addition between {T1} and {T2} is not valid at position {pos}")
        )
    ;

    bool AdditionPrime(out AnnotatedNode<Annotations>? Node)
        => BinaryPrime(
            NextInPriority: Multiplication,
            Operators: [TokenType.Addition, TokenType.Subtraction],
            CurrentProductionName: nameof(AdditionPrime),
            out Node,
            MessageOnError: (_) => "",
            Action: GetBinaryPrimeAction((T1, T2, pos) => $"Addition between {T1} and {T2} is not valid at position {pos}")
        )
    ;
    // AdditionPrime ::= ("-" | "+") Multiplication AdditionPrime | Empty
    bool Multiplication(out AnnotatedNode<Annotations>? Node)
        => PrimedBinary(
            NextInPriority: Power,
            BinaryPrime: MultiplicationPrime,
            CurrentProductionName: nameof(Multiplication),
            out Node,
            ErrorMessage: (_) => "",
            Action: GetPrimedBinaryAction((T1, T2, Pos) => $"Multiplication is not valid between {T1} and {T2} at {Pos}")
        )
    ;
    bool MultiplicationPrime(out AnnotatedNode<Annotations>? Node)
        => BinaryPrime(
            NextInPriority: Power,
            Operators: [TokenType.Multiplication, TokenType.Division],
            CurrentProductionName: nameof(MultiplicationPrime),
            out Node,
            MessageOnError: (_) => "",
            Action: GetBinaryPrimeAction((T1, T2, Pos) => $"Multiplication is not valid between {T1} and {T2} MultiplicationPrime at Pos {Position}")
        )
    ;


    bool Power(out AnnotatedNode<Annotations>? Node)
        => PrimedBinary(
            NextInPriority: Negation,
            BinaryPrime: PowerPrime,
            CurrentProductionName: nameof(Power),
            out Node,
            ErrorMessage: (_) => "",
            Action: GetPrimedBinaryAction((T1, T2, Pos) => $"Exponentiation is not valid between {T1} and {T2} at Position {Pos}")
        )
    ;
    bool PowerPrime(out AnnotatedNode<Annotations>? Node)
        => BinaryPrime(
            NextInPriority: Negation,
            Operators: [TokenType.Exponentiation],
            CurrentProductionName: nameof(PowerPrime),
            Node: out Node,
            MessageOnError: (_) => "",
            Action: GetBinaryPrimeAction((T1, T2, Pos) => $"Exponentiation is not valid between {T1} and {T2} at Position {Pos}")
        )
    ;
    bool Negation(out AnnotatedNode<Annotations>? Node)
    {
        if (Input[Current].TT == TokenType.Subtraction)
        {
            IToken Operator = Input[Current];
            Current++;
            if (SP.SafeParse(Expression, out AnnotatedNode<Annotations>? Expr, Suppress: false, Current: ref Current))
            {
                Node = new(
                    Attributes: Expr!.Attributes.Copy(), //Since Unary Negation does not change type at all, we can just copy - will introduce unary table at some point
                    node: ASTNode.Unary(Operator: Operator, Operand: Expr!, nameof(Negation)));
                return true;
            }
        }
        else if (SP.SafeParse(Primary, out AnnotatedNode<Annotations>? PrimaryNode, Suppress: false, Current: ref Current))
        {
            Node = new(
                Attributes: PrimaryNode!.Attributes.Copy(), //single nest so we can just copy
                node: ASTNode.NonTerminal(PrimaryNode!, nameof(Negation))
            );
            return true;
        }
        //we can use current here because being here means primary also failed, and thus current is rolled back
        Log.Log($"Expected \"-\" or Primary at Token Position {Position}, but got \"{Input[Current].Lexeme}\"; Error may be here, or at Primary:");

        Node = null;
        return false;
    }
    bool Primary(out AnnotatedNode<Annotations>? Node) //assuming scanner can currently only scan floats; TODO: Upgrade scanner
    {
        if (Input[Current].TT == TokenType.OpenParen)
        {
            IToken Operator = Input[Current];
            Current++;
            if (SP.SafeParse(Expression, out AnnotatedNode<Annotations>? Expr, Suppress: false, Current: ref Current))
            {
                if (Input[Current].TT == TokenType.CloseParen)
                {
                    Annotations annotations = new Annotations(CanBeResolvedToAssignable: false);
                    annotations.ForcedMerge(Expr!.Attributes); //we propagate empty since empty brackets are still empty, but the addition of brackets means that we can no-longer resolve to an identifier literal
                    Node = new(
                        Attributes: annotations,
                        node: ASTNode.Parenthesized(Open: Operator, Center: Expr!, Close: Input[Current], nameof(Primary))
                    );
                    Current++;
                    return true;
                }
                else
                {
                    Log.Log($"Expected Close Parenthesis at position {Position}");
                    Node = null;
                    return false;
                }
            }
            else
            {
                //no message as not suppressed
                Node = null;
                return false;
            }
        }
        else if (CurrentToken().TCmp(TokenType.Identifier))
        {
            IToken IdentifierToken = Input[Current++];
            ASTNode IdentifierNode = ASTNode.Terminal(IdentifierToken, nameof(Primary));
            if (SP.SafeParse(AssignmentPrime, out AnnotatedNode<Annotations>? AssP, Current: ref Current))
            {
                ASTNode ASNode = ASTNode.PrimedBinary(IdentifierNode, AssP!, nameof(Primary));
                uint? IdentifierType = TP.GetTypeFromIdentifierLiteral(IdentifierToken.Lexeme);
                if (IdentifierType is null)
                {
                    Log.Log($"Identifier {IdentifierToken.Lexeme} was used before declaration at {Position}");
                    Node = null;
                    return false;
                }
                if (AssP!.Attributes.IsEmpty is true)
                {
                    Node = new(
                        Attributes: new(
                            TypeCode: IdentifierType,
                            CanBeResolvedToAssignable: true,
                            IsEmpty: false),
                        node: AssP
                    );
                    return true;
                }
                else
                {
                    Debug.Assert(AssP!.Attributes.TypeCode is not null);
                    //typecheck
                    if (TP.CanBeAssignedTo((uint)IdentifierType, (uint)AssP!.Attributes.TypeCode))
                    {
                        Node = new(
                            Attributes: new(TypeCode: IdentifierType, IsEmpty: false, TypeDenotedByIdentifier: null), //TypeDenotedByIdentifier would need a lookup if we had custom types but we don't
                            node: AssP
                        );
                        return true;
                    }
                    else
                    {
                        Log.Log($"Could not assign {AssP!.Attributes.TypeCode} to {IdentifierType} at {Position}. If you meant to cast, declare the variable in-line; if you do not want to bind, use _ as the identifier.");
                        Node = null;
                        return false;
                    }
                }
            }
            Node = null;
            return false;
        }
        else if (CurrentToken().TCmp(TokenType.Number))
        {
            IToken NumberToken = Input[Current++];
            ASTNode NumberNode = ASTNode.Terminal(NumberToken, nameof(Primary));
            Node = new(
                new(TypeCode: TP.GetTypeFromNumberLiteral(NumberToken.Lexeme)),
                NumberNode
            );
            return true;
        }
        Log.Log($"Expected Open Parenthesis ( or Number or identifier at token position {Position}, but got \"{Input[Current].Lexeme}\"");
        Node = null;
        return false;
    }
    bool Type(out AnnotatedNode<Annotations>? Node)
    {
        if (CurrentToken().TCmp([TokenType.TypeByte, TokenType.TypeDouble, TokenType.TypeInt, TokenType.TypeLong, TokenType.TypeLongInt, TokenType.TypeFloat, TokenType.TypeNumber]))
        {
            Node = new(
                new(
                    CanBeResolvedToAssignable: false, //even though it *can*, we can't assign
                    TypeDenotedByIdentifier: TP.GetTypeFromTypeDenotingIdentifier(Input[Current].Lexeme),
                    TypeCode: null
                ),
                ASTNode.Terminal(Input[Current], nameof(Type)));
            Current++;
            return true;
        }
        Log.Log($"Expected valid Type at {Position}");
        Node = null;
        return false;
    }

}