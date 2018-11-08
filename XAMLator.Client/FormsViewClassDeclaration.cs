﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xamarin.Forms.Build.Tasks;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace XAMLator.Client
{
	/// <summary>
	/// Represents a Xamarin Forms View class including the XAML view,
	/// the code-behind, the autogenerated code and other partial declarations
	/// of the class.
	/// When the class is updated with the current status of the class when
	/// the IDE notifies changes on any of the documents that composes this class.
	/// This tool was used to easilly generate the Roslyn code from sources
	/// https://roslynquoter.azurewebsites.net/
	/// </summary>
	public class FormsViewClassDeclaration
	{

		/// <summary>
		/// The xaml classes.
		/// </summary>
		static readonly List<FormsViewClassDeclaration> classesCache = new List<FormsViewClassDeclaration>();

		/// <summary>
		/// Tries to find a cached class declaration with the same full namespace.
		/// </summary>
		/// <returns><c>true</c>, if we found matching class, <c>false</c> otherwise.</returns>
		/// <param name="fullNamespace">Full namespace.</param>
		/// <param name="viewClass">View class.</param>
		internal static bool TryGetByFullNamespace(string fullNamespace, out FormsViewClassDeclaration viewClass)
		{
			viewClass = classesCache.SingleOrDefault(x => x.FullNamespace == fullNamespace);
			return viewClass != null;
		}

		/// <summary>
		/// Tries to find a cached class declaration using this file path.
		/// </summary>
		/// <returns><c>true</c>, if we found matching class, <c>false</c> otherwise.</returns>
		/// <param name="filePath">File path.</param>
		/// <param name="viewClass">View class.</param>
		internal static bool TryGetByFileName(string filePath, out FormsViewClassDeclaration viewClass)
		{
			viewClass = classesCache.SingleOrDefault(x => x.xamlFilePath == filePath || x.codeBehindFilePath == filePath);
			return viewClass != null;
		}

		static ConstructorInfo xamlGeneratorConstructor;
		static MethodInfo executeMethod;
		string autoGenCodeBehindCode;
		string xamlFilePath;
		string codeBehindFilePath;
		string autoGenCodeBehindFilePath;
		int counter = 0;
		ClassDeclarationSyntax classDeclarationSyntax;
		SemanticModel model;
		List<SyntaxTree> sources;
		List<MemberDeclarationSyntax> partials;
		List<string> usings;
		ISymbol symbol;

		static FormsViewClassDeclaration()
		{
			var assembly = Assembly.GetAssembly(typeof(XamlGTask));
			var type = assembly.GetType("Xamarin.Forms.Build.Tasks.XamlGenerator");
			xamlGeneratorConstructor = type.GetConstructors().Single(c => c.GetParameters().Length == 7);
			executeMethod = type.GetMethod("Execute");
		}

		/// <summary>
		/// Initializes a new instance of the <see cref="T:XAMLator.Client.FormsViewClassDeclaration"/> class
		/// from a code behind document.
		/// </summary>
		/// <param name="classDeclarationSyntax">Class declaration syntax.</param>
		/// <param name="model">Model.</param>
		/// <param name="codeBehindFilePath">Code behind file path.</param>
		/// <param name="xaml">Xaml.</param>
		public FormsViewClassDeclaration(ClassDeclarationSyntax classDeclarationSyntax, SemanticModel model,
										 string codeBehindFilePath, XAMLDocument xaml)
		{
			this.codeBehindFilePath = codeBehindFilePath;
			this.xamlFilePath = xaml.FilePath;
			StyleSheets = new Dictionary<string, string>();
			FillClassInfo(classDeclarationSyntax, model);
			UpdateXaml(xaml);
			UpdateCode(classDeclarationSyntax);
			classesCache.Add(this);
		}

		/// <summary>
		/// Initializes a new instance of the <see cref="T:XAMLator.Client.FormsViewClassDeclaration"/> class
		/// from a XAML document.
		/// </summary>
		/// <param name="codeBehindFilePath">Code behind file path.</param>
		/// <param name="xaml">XAML.</param>
		public FormsViewClassDeclaration(string codeBehindFilePath, XAMLDocument xaml)
		{
			this.codeBehindFilePath = codeBehindFilePath;
			this.xamlFilePath = xaml.FilePath;
			StyleSheets = new Dictionary<string, string>();
			Namespace = xaml.Type.Substring(0, xaml.Type.LastIndexOf('.'));
			ClassName = xaml.Type.Split('.').Last();
			classesCache.Add(this);
			UpdateXaml(xaml);
		}

		/// <summary>
		/// Gets the name of the class.
		/// </summary>
		/// <value>The name of the class.</value>
		public string ClassName { get; private set; }

		/// <summary>
		/// Gets the namespace.
		/// </summary>
		/// <value>The namespace.</value>
		public string Namespace { get; private set; }

		/// <summary>
		/// Gets the full namespace including the class name.
		/// </summary>
		/// <value>The full namespace.</value>
		public string FullNamespace
		{
			get
			{
				if (String.IsNullOrWhiteSpace(Namespace))
				{
					return ClassName;
				}
				return $"{Namespace}.{ClassName}";
			}
		}

		/// <summary>
		/// Gets a value indicating whether this instance was created from a XAML
		/// document and still needs it's class initialization.
		/// </summary>
		/// <value><c>true</c> if needs class initialization; otherwise, <c>false</c>.</value>
		public bool NeedsClassInitialization { get; private set; } = true;

		/// <summary>
		/// Gets the expression to build a new instance of the class.
		/// </summary>
		/// <value>The new type expression.</value>
		public string NewInstanceExpression => $"new {CurrentFullNamespace} ()";

		/// <summary>
		/// Gets the XAML of the view
		/// </summary>
		/// <value>The xaml.</value>
		public string Xaml { get; private set; }

		/// <summary>
		/// Gets the combined and updated code of the view.
		/// </summary>
		/// <value>The code.</value>
		public string Code { get; private set; }

		/// <summary>
		/// Gets a value indicating whether the code of the view has changed
		/// and it needs to be rebuilt.
		/// </summary>
		/// <value><c>true</c> if needs rebuild; otherwise, <c>false</c>.</value>
		public bool NeedsRebuild { get; private set; }

		/// <summary>
		/// Gets the xaml resource identifier.
		/// </summary>
		/// <value>The xaml resource identifier.</value>
		// FIXME: This must be retrieved using the XamlResourceIdAttribute in the autogenerated
		// code behind
		public string XamlResourceId => xamlFilePath != null ? Path.GetFileName(xamlFilePath) : null;

		string CurrentClassName => counter == 0 ? ClassName : $"{ClassName}{counter}";

		string CurrentFullNamespace
		{
			get
			{
				if (String.IsNullOrWhiteSpace(Namespace))
				{
					return CurrentClassName;
				}
				return $"{Namespace}.{CurrentClassName}";
			}
		}

		public Dictionary<string, string> StyleSheets { get; set; }

		/// <summary>
		/// Updates the XAML of the class declaration.
		/// </summary>
		/// <param name="xaml">Xaml.</param>
		internal async Task UpdateXaml(XAMLDocument xaml)
		{
			Xaml = xaml.XAML;
			LoadStyleSheets(xaml);
			await UpdateAutoGeneratedCodeBehind();
			autoGenCodeBehindCode = null;
			FillAutoGenCodeBehind();
		}

		void LoadStyleSheets(XAMLDocument xaml)
		{
			foreach (var styleSheetPath in xaml.StyleSheets)
			{
				try
				{
					StyleSheets[styleSheetPath] = File.ReadAllText(ResolveStyleSheetPath(xaml, styleSheetPath));
				}
				catch (Exception ex)
				{
					Log.Exception(ex);
				}
			}
		}

		string ResolveStyleSheetPath(XAMLDocument xaml, string styleSheetPath)
		{
			if (styleSheetPath.StartsWith(Constants.ROOT_REPLACEMENT))
			{
				styleSheetPath = styleSheetPath.Replace(Constants.ROOT_REPLACEMENT + "/", "");
				var currentDir = Path.GetDirectoryName(xaml.FilePath);
				do
				{
					var filePath = Path.Combine(currentDir, styleSheetPath);
					if (File.Exists(filePath))
					{
						return filePath;
					}
					currentDir = Path.GetFullPath(Path.Combine(currentDir, ".."));
				} while (Directory.GetFiles(currentDir).Any(f => f.EndsWith(".sln"))
						 || currentDir == Directory.GetDirectoryRoot(currentDir));
				return null;
			}
			else
			{
				return Path.Combine(Path.GetDirectoryName(xaml.FilePath), styleSheetPath);
			}
		}

		/// <summary>
		/// Fills the class info.
		/// </summary>
		/// <param name="classDeclarationSyntax">Class declaration syntax.</param>
		/// <param name="model">Model.</param>
		internal void FillClassInfo(ClassDeclarationSyntax classDeclarationSyntax, SemanticModel model)
		{
			this.classDeclarationSyntax = classDeclarationSyntax;
			this.model = model;
			FindSymbol();
			FillName();
			FillNamespace();
			FillSources();
			NeedsClassInitialization = false;
		}

		/// <summary>
		/// Updates the code behind of the class declaration.
		/// </summary>
		/// <param name="classDeclarationSyntax">Class declaration syntax.</param>
		internal void UpdateCode(ClassDeclarationSyntax classDeclarationSyntax)
		{
			this.classDeclarationSyntax = classDeclarationSyntax;
			FillSources();
			FillUsings();
			FillPartials();
			FillAutoGenCodeBehind();
			FillCode();
		}

		/// <summary>
		/// Finds the symbol representing the class.
		/// </summary>
		void FindSymbol()
		{
			symbol = model.GetDeclaredSymbol(classDeclarationSyntax);
		}

		/// <summary>
		/// Fills the name of the class.
		/// </summary>
		void FillName()
		{
			ClassName = classDeclarationSyntax.Identifier.Text;
		}

		/// <summary>
		/// Get the namespace of the class.
		/// </summary>
		void FillNamespace()
		{
			Namespace = classDeclarationSyntax.Ancestors()
				   .OfType<NamespaceDeclarationSyntax>()
				   .Select(n => n.Name.GetText().ToString().Trim())
						  .FirstOrDefault() ?? "";
		}

		/// <summary>
		/// Fills all the sources where this class is defined which can be one
		/// for a regular class or many for partial ones.
		/// </summary>
		void FillSources()
		{
			sources = symbol.Locations.Select(l => l.SourceTree).ToList();
			autoGenCodeBehindFilePath = sources
				.Select(s => s.FilePath)
				.SingleOrDefault(s => s.EndsWith("g.cs"));
		}

		/// <summary>
		/// Fills code in partial classes excep the autogenerated one.
		/// </summary>
		void FillPartials()
		{
			partials = symbol.Locations
							 .Where(l =>
									l.SourceTree.FilePath != codeBehindFilePath &&
									l.SourceTree.FilePath != autoGenCodeBehindFilePath)
							 .SelectMany(l => FindClass(l.SourceTree, classDeclarationSyntax.Identifier.Text).Members)
							 .ToList();
		}

		/// <summary>
		/// Fills the partial class in the auto generated code.
		/// </summary>
		void FillAutoGenCodeBehind()
		{
			if (!File.Exists(autoGenCodeBehindFilePath))
			{
				return;
			}
			if (String.IsNullOrEmpty(autoGenCodeBehindCode))
			{
				autoGenCodeBehindCode = File.ReadAllText(autoGenCodeBehindFilePath);
			}
			var syntaxTree = CSharpSyntaxTree.ParseText(autoGenCodeBehindCode);
			var newClass = RewriteAutogeneratedCodeConstructor(FindClass(syntaxTree, ClassName));
			var members = newClass.Members;
			partials.AddRange(members);
		}

		/// <summary>
		/// Fills all the usings requiered for this class.
		/// </summary>
		void FillUsings()
		{
			usings = sources.SelectMany(s => s.GetRoot()
										.DescendantNodes()
										.OfType<UsingDirectiveSyntax>()
										.Select(u => u.GetText().ToString()))
							.Distinct()
							.ToList();
		}

		/// <summary>
		/// Fills the code of the class combining all partials and removing comments.
		/// </summary>
		void FillCode()
		{
			var modifiersExceptPartial = TokenList(
				classDeclarationSyntax.Modifiers.Where(m => !m.IsKind(SyntaxKind.PartialKeyword)));
			var newIdentifier = Identifier(ClassName);

			var fullClass = classDeclarationSyntax
				.WithModifiers(modifiersExceptPartial)
				.AddMembers(partials.ToArray())
				.NormalizeWhitespace();

			var lines = fullClass.GetText().Lines.Select(l => l.ToString());
			var code = $"{String.Join("", usings)}\nnamespace {Namespace}\n{{\n {String.Join("\n", lines)} \n}}";
			// Make sure we only replace the class declaration and the constructors
			code = code.Replace($" {ClassName} ", $" {ClassName}{counter + 1} ");
			code = code.Replace($" {ClassName}(", $" {ClassName}{counter + 1}(");
			if (code != Code)
			{
				Code = code;
				NeedsRebuild = true;
				counter++;
			}
		}

		/// <summary>
		/// Updates the auto generated code behind when there is a change
		/// in the XAML as it might update local variable or events.
		/// </summary>
		async Task UpdateAutoGeneratedCodeBehind()
		{
			// FIXME: I couldn't manage to get this working with MonoDevelop
			// return XAMLatorMonitor.Instance.IDE.RunTarget("XamlG");

			if (String.IsNullOrEmpty(autoGenCodeBehindFilePath))
			{
				return;
			}
			try
			{
				object[] parameters = {xamlFilePath, "C#", FullNamespace + ".xaml",
					xamlFilePath, null, autoGenCodeBehindFilePath, null};
				var generator = xamlGeneratorConstructor.Invoke(parameters);
				executeMethod.Invoke(generator, null);
			}
			catch (Exception ex)
			{
				Log.Exception(ex);
			}
		}

		/// <summary>
		/// Rewrites the autogenerated code constructor replace the LoadFromXaml
		/// call that reads the XAML fro the assembly resources for a call to
		/// <see cref="XAMLator.Server.VM.LoadXaml"/> that loads the XAML from
		/// the <see cref="EvalRequest"/>
		/// </summary>
		/// <returns>The autogenerated code constructor.</returns>
		/// <param name="classDeclaration">Class declaration.</param>
		ClassDeclarationSyntax RewriteAutogeneratedCodeConstructor(ClassDeclarationSyntax classDeclaration)
		{
			var loadFromXaml = classDeclaration.DescendantNodes()
											   .OfType<ExpressionStatementSyntax>()
											   .Single(e => e.Expression.ToString().Contains("LoadFromXaml"));

			var newLoadXaml = ExpressionStatement(
				InvocationExpression(
					IdentifierName("XAMLator.Server.VM.LoadXaml"))
				.WithArgumentList(
					ArgumentList(
						SingletonSeparatedList(
						Argument(ThisExpression())))));

			return classDeclaration.ReplaceNode(loadFromXaml, newLoadXaml);
		}

		/// <summary>
		/// Finds a class for the given name in a syntax tree.
		/// </summary>
		/// <returns>The class.</returns>
		/// <param name="syntaxTree">Syntax tree.</param>
		/// <param name="className">Class name.</param>
		public static ClassDeclarationSyntax FindClass(SyntaxTree syntaxTree, string className)
		{
			return syntaxTree.GetRoot()
							 .DescendantNodes()
							 .OfType<ClassDeclarationSyntax>()
							 .Single(c => c.Identifier.Text == className);
		}
	}
}
