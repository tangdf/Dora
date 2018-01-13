﻿using Dora.DynamicProxy.Properties;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using System.Threading.Tasks;

namespace Dora.DynamicProxy
{
    public class DynamicProxyClassGenerator
    {
        #region Properties
        public Type TypeToIntercept { get; }
        public InterceptorDecoration Interceptors { get; } 
        public ModuleBuilder ModuleBuilder { get; }
        public TypeBuilder TypeBuilder { get; }
        public FieldBuilder TargetFiled { get; }
        public FieldBuilder InterceptorsField { get; }
        #endregion

        public static DynamicProxyClassGenerator CreateInterfaceGenerator(Type @interface, InterceptorDecoration interceptors)
        {
            Guard.ArgumentNotNull(@interface, nameof(@interface));
            if (!@interface.IsInterface)
            {
                throw new ArgumentException(Resources.ExceptionArgumentNotInterface, nameof(@interface));
            }
            return new DynamicProxyClassGenerator(@interface, interceptors);
        }

        public static DynamicProxyClassGenerator CreateVirtualMethodGenerator(Type nonSealedType, InterceptorDecoration interceptors)
        {
            Guard.ArgumentNotNull(nonSealedType, nameof(nonSealedType));
            if (nonSealedType.IsSealed)
            {
                throw new ArgumentException(Resources.ExceptionSealedTypeNotAllowed, nameof(nonSealedType));
            }
            return new DynamicProxyClassGenerator(nonSealedType, interceptors);
        }

        public Type GenerateProxyType()
        {
            if (this.TypeToIntercept.IsInterface)
            {
                this.GenerateForInterface();
            }
            else
            {
                this.GenerateForVirtualMethods();
            }

            return this.TypeBuilder.CreateTypeInfo();
        }

        private DynamicProxyClassGenerator(Type typeToIntercept, InterceptorDecoration interceptors)
        {
            this.TypeToIntercept = Guard.ArgumentNotNull(typeToIntercept, nameof(typeToIntercept));
            this.Interceptors = Guard.ArgumentNotNull(interceptors, nameof(interceptors));

            var assemblyName = new AssemblyName($"AssemblyFor{typeToIntercept.Name}{GenerateSurfix()}");
            var assemblyBuilder = AssemblyBuilder.DefineDynamicAssembly(assemblyName, AssemblyBuilderAccess.Run);
            this.ModuleBuilder = assemblyBuilder.DefineDynamicModule($"{assemblyName}.dll");

            if (this.TypeToIntercept.IsInterface)
            {
                this.TypeBuilder = this.ModuleBuilder.DefineType($"{typeToIntercept.Name}{GenerateSurfix()}", TypeAttributes.Public, typeof(object), new Type[] { typeToIntercept });
                this.TargetFiled = this.TypeBuilder.DefineField("_target", typeToIntercept, FieldAttributes.Private);
            }
            else
            {
                this.TypeBuilder = this.ModuleBuilder.DefineType($"{typeToIntercept.Name}{GenerateSurfix()}", TypeAttributes.Public, typeToIntercept);
            }
            this.InterceptorsField = this.TypeBuilder.DefineField("_interceptors", typeof(InterceptorDecoration), FieldAttributes.Private);
        }

        private void GenerateForInterface()
        {
            var attributes = MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.NewSlot | MethodAttributes.Virtual | MethodAttributes.Final;
            this.DefineConstructor();
            foreach (var methodInfo in this.TypeToIntercept.GetMethods().Where(it => !it.IsSpecialName))
            {
                if (this.Interceptors.Contains(methodInfo))
                {
                    this.DefineInterceptableMethod(methodInfo, attributes);
                }
                else
                {
                    this.DefineNonInterceptableMethod(methodInfo, attributes);
                }
            }
            foreach (var property in this.TypeToIntercept.GetProperties())
            {
                var parameterTypes = property.GetIndexParameters().Select(it => it.ParameterType).ToArray();
                var propertyBuilder = this.TypeBuilder.DefineProperty(property.Name, property.Attributes, property.PropertyType, parameterTypes);
                var getMethod = property.GetMethod;
                if (null != getMethod)
                {
                    var getMethodBuilder = this.Interceptors.IsInterceptable(getMethod)
                        ? this.DefineInterceptableMethod(getMethod, attributes)
                        : this.DefineNonInterceptableMethod(getMethod, attributes);
                    propertyBuilder.SetGetMethod(getMethodBuilder);
                }
                var setMethod = property.SetMethod;
                if (null != setMethod)
                {
                    var setMethodBuilder = this.Interceptors.IsInterceptable(setMethod)
                        ? this.DefineInterceptableMethod(setMethod, attributes)
                        : this.DefineNonInterceptableMethod(setMethod, attributes);
                    propertyBuilder.SetGetMethod(setMethodBuilder);
                }
            }

            foreach (var eventInfo in this.TypeToIntercept.GetEvents())
            {
                var eventBuilder = this.TypeBuilder.DefineEvent(eventInfo.Name, eventInfo.Attributes, eventInfo.EventHandlerType);
                eventBuilder.SetAddOnMethod(this.DefineNonInterceptableMethod(eventInfo.AddMethod, attributes));
                eventBuilder.SetRemoveOnMethod(this.DefineNonInterceptableMethod(eventInfo.RemoveMethod, attributes));
            }                                                         
        }

        private void GenerateForVirtualMethods()
        {
            this.DefineConstructor();
            foreach (var methodInfo in this.TypeToIntercept.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (methodInfo.IsVirtual && this.Interceptors.Contains(methodInfo))
                {
                    var attributes = this.GetMethodAttributes(methodInfo);
                    if (null != attributes)
                    {
                        this.DefineInterceptableMethod(methodInfo, attributes.Value);
                    }
                }
            }

        }
        protected virtual void DefineConstructor()
        {
            var parameterTypes = this.TypeToIntercept.IsInterface
                ? new Type[] { this.TypeToIntercept, typeof(InterceptorDecoration) }
                : new Type[] { typeof(InterceptorDecoration) };
            var constructor = this.TypeBuilder.DefineConstructor(MethodAttributes.Public, CallingConventions.HasThis, parameterTypes);
            var il = constructor.GetILGenerator();

            //Call object's constructor.
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Call, ReflectionUtility.ConstructorOfObject);

            if (this.TypeToIntercept.IsInterface)
            {
                //Set _target filed
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldarg_1);
                il.Emit(OpCodes.Stfld, this.TargetFiled);

                //Set _interceptors field
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldarg_2);
                il.Emit(OpCodes.Stfld, this.InterceptorsField);
            }
            else
            {
                //Set _interceptors field
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldarg_1);
                il.Emit(OpCodes.Stfld, this.InterceptorsField);
            }

            //Return
            il.Emit(OpCodes.Ret);
        }

        protected virtual MethodBuilder DefineInterceptableMethod(MethodInfo methodInfo, MethodAttributes attributes)
        {
            Guard.ArgumentNotNull(methodInfo, nameof(methodInfo));
            var parameters = methodInfo.GetParameters();
            var targetInvokerType = this.DefineTargetInvoker(methodInfo);
            var parameterTypes = parameters.Select(it => it.ParameterType).ToArray();                                                                              
            var methodBuilder = this.TypeBuilder.DefineMethod(methodInfo.Name, attributes, methodInfo.ReturnType, parameterTypes);
            methodBuilder.SetParameters(parameters.Select(it => it.ParameterType).ToArray());
            if (methodInfo.IsGenericMethod)
            {
                this.DefineMethodGenericParameters(methodBuilder, methodInfo);
            }
            for (int index = 0; index < parameters.Length; index++)
            {
                var parameter = parameters[index];
                methodBuilder.DefineParameter(index + 1, parameter.Attributes, parameter.Name);
            } 

            var il = methodBuilder.GetILGenerator();

            var handler = il.DeclareLocal(typeof(InterceptDelegate));
            var interceptor = il.DeclareLocal(typeof(InterceptorDelegate));
            var arguments = il.DeclareLocal(typeof(object[]));
            var methodBase = il.DeclareLocal(typeof(MethodBase));
            var invocationContext = il.DeclareLocal(typeof(InvocationContext));
            var task = il.DeclareLocal(typeof(Task));
            var returnType = methodInfo.ReturnTaskOfResult()? methodInfo.ReturnType.GetGenericArguments()[0]: methodInfo.ReturnType;

            LocalBuilder returnValueAccessor = null;
            LocalBuilder func = null;
            if (methodInfo.ReturnType != typeof(void))
            {
                returnValueAccessor = il.DeclareLocal(typeof(ReturnValueAccessor<>).MakeGenericType(returnType));
                func = il.DeclareLocal(typeof(Func<,>).MakeGenericType(typeof(Task), returnType));
            } 

            //New object[] for InvocationContext.Arguments
            il.EmitLoadConstantInt32(parameters.Length);
            il.Emit(OpCodes.Newarr, typeof(object));

            //Load arguments and store them to object[]
            for (int index = 0; index < parameters.Length; index++)
            {
                var parameter = parameters[index];
                il.Emit(OpCodes.Dup);
                il.EmitLoadConstantInt32(index);
                il.EmitLoadArgument(index);    
                if (parameter.ParameterType.IsByRef)
                {
                    il.EmitLdInd(parameter.ParameterType);
                }
                il.EmitBox(parameter.ParameterType);
                il.Emit(OpCodes.Stelem_Ref);
            }                   
            il.Emit(OpCodes.Stloc, arguments);     

            //Load and store current method
            il.Emit(OpCodes.Ldtoken, methodInfo);
            if (methodInfo.DeclaringType.IsGenericType)
            {
                il.Emit(OpCodes.Ldtoken, methodInfo.DeclaringType);
                il.Emit(OpCodes.Call, ReflectionUtility.GetMethodFromHandleMethodOfMethodBase2);
            }
            else
            {
                il.Emit(OpCodes.Call, ReflectionUtility.GetMethodFromHandleMethodOfMethodBase1);
            }
            il.Emit(OpCodes.Stloc, methodBase);             

            //Create and store DefaultInvocationContext
            il.Emit(OpCodes.Ldloc, methodBase);
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldarg_0);
            if (this.TypeToIntercept.IsInterface)
            {
                il.Emit(OpCodes.Ldfld, this.TargetFiled);
            }
            il.Emit(OpCodes.Ldloc, arguments);
            il.Emit(OpCodes.Newobj, ReflectionUtility.ConstructorOfDefaultInvocationContext);
            il.Emit(OpCodes.Stloc, invocationContext);    

            //Get and store current method specific interceptor
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldfld, this.InterceptorsField);
            il.Emit(OpCodes.Ldloc, methodBase);
            il.Emit(OpCodes.Callvirt, InterceptorDecoration.MethodOfgetInterceptor);
            il.Emit(OpCodes.Stloc, interceptor);

            //Create and store handler to invoke target method
            il.Emit(OpCodes.Ldarg_0);
            if (this.TypeToIntercept.IsInterface)
            {
                il.Emit(OpCodes.Ldfld, this.TargetFiled);
            }
            if (methodInfo.IsGenericMethod)
            {
                var genericTargetInvokerType = targetInvokerType.MakeGenericType(methodInfo.GetGenericArguments());
                var constructor = genericTargetInvokerType.GetConstructor(new Type[] { methodInfo.DeclaringType });
                var invokeMethod = genericTargetInvokerType.GetMethod("Invoke", new Type[] { typeof(InvocationContext) });
                il.Emit(OpCodes.Newobj, constructor);
                il.Emit(OpCodes.Ldftn, invokeMethod);
            }
            else
            {
                var constructor = targetInvokerType.GetConstructor(new Type[] { methodInfo.DeclaringType });
                var invokeMethod = targetInvokerType.GetMethod("Invoke", new Type[] { typeof(InvocationContext) });
                il.Emit(OpCodes.Newobj, constructor);
                il.Emit(OpCodes.Ldftn, invokeMethod);
            }
            il.Emit(OpCodes.Newobj, ReflectionUtility.ConstructorOfInterceptDelegate);
            il.Emit(OpCodes.Stloc, handler); 

            //Invoke the interceptor and store the result (an InterceptDelegate object) as handler. 
            il.Emit(OpCodes.Ldloc, interceptor);
            il.Emit(OpCodes.Ldloc, handler);
            il.Emit(OpCodes.Callvirt, ReflectionUtility.InvokeMethodOfInterceptorDelegate);
            il.Emit(OpCodes.Stloc, handler);

            //Invoke the the final handler and store the returned Task
            il.Emit(OpCodes.Ldloc, handler);
            il.Emit(OpCodes.Ldloc, invocationContext);
            il.Emit(OpCodes.Callvirt, ReflectionUtility.InvokeMethodOfInterceptDelegate);
            il.Emit(OpCodes.Stloc, task);

            //When return Task<TResult>
            if (methodInfo.ReturnTaskOfResult())
            {
                //Create and store ReturnValueAccessor<Return>
                il.Emit(OpCodes.Ldloc, invocationContext);
                il.Emit(OpCodes.Newobj, ReflectionUtility.GetConstructorOfRetureValueAccessor(returnType));
                il.Emit(OpCodes.Stloc, returnValueAccessor);

                //Create a Func<Task, TReturn> to represent the ReturnValueAccessor<Return>.GetReturnValue
                il.Emit(OpCodes.Ldloc, returnValueAccessor);
                il.Emit(OpCodes.Ldftn, ReflectionUtility.GetMethodsOfGetReturnValue(returnType));
                il.Emit(OpCodes.Newobj, ReflectionUtility.GetConstructorOfFuncOfTaskAndReturnValue(returnType));
                il.Emit(OpCodes.Stloc, func);

                //Invoke handler's ContinueWith 
                il.Emit(OpCodes.Ldloc, task);
                il.Emit(OpCodes.Ldloc, func);
                il.Emit(OpCodes.Callvirt, ReflectionUtility.GetMethodOfContiueWithMethodOfTask(returnType));
                il.Emit(OpCodes.Ret);
                return methodBuilder;
            }

            //When return Task
            if (methodInfo.ReturnTask())
            {
                il.Emit(OpCodes.Ldloc, task);
                il.Emit(OpCodes.Ret);
                return methodBuilder;
            }

            il.Emit(OpCodes.Ldloc, task);
            il.Emit(OpCodes.Callvirt, ReflectionUtility.WaitMethodOfTask);

            if (parameters.Any(it => it.ParameterType.IsByRef))
            {
                for (int index = 0; index < parameters.Length; index++)
                {
                    var parameter = parameters[index];
                    if (parameter.ParameterType.IsByRef)
                    {
                        il.EmitLoadArgument(index);
                        il.Emit(OpCodes.Ldloc, arguments);
                        il.EmitLoadConstantInt32(index);
                        il.Emit(OpCodes.Ldelem_Ref);
                        il.EmitUnboxOrCast(parameter.ParameterType);
                        il.EmitStInd(parameter.ParameterType);
                    }
                }
            }

            // When return void
            if (methodInfo.ReturnVoid())
            {
                il.Emit(OpCodes.Ret);
            }

            il.Emit(OpCodes.Ldloc, invocationContext);
            il.Emit(OpCodes.Callvirt, ReflectionUtility.ReturnValueOfInvocationContext.GetMethod);
            il.EmitUnboxOrCast(methodInfo.ReturnType);    
            il.Emit(OpCodes.Ret);
            return methodBuilder;
        }

        protected virtual MethodBuilder DefineNonInterceptableMethod(MethodInfo methodInfo, MethodAttributes attributes)
        {
            var parameters = methodInfo.GetParameters();
            //var targetInvoker = this.DefineTargetInvoker(typeBuilder, method);
            var parameterTypes = parameters.Select(it => it.ParameterType).ToArray();
            var methodBuilder = this.TypeBuilder.DefineMethod(methodInfo.Name, attributes, methodInfo.ReturnType, parameterTypes);
            if (methodInfo.IsGenericMethod)
            {
                this.DefineMethodGenericParameters(methodBuilder, methodInfo);
            }
            for (int index = 0; index < parameters.Length; index++)
            {
                var parameter = parameters[index];
                methodBuilder.DefineParameter(index, parameter.Attributes, parameter.Name);
            } 
            var il = methodBuilder.GetILGenerator();

            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldfld, this.TargetFiled);
            for (int index = 0; index < parameters.Length; index++)
            {
                var parameter = parameters[index];
                il.EmitLoadArgument(index); 
            }
            il.Emit(OpCodes.Callvirt, methodInfo);
            il.Emit(OpCodes.Ret);
            return methodBuilder;
        }

        protected virtual MethodAttributes? GetMethodAttributes(MethodInfo methodInfo)
        {
            if (this.TypeToIntercept.IsInterface)
            {
                return MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.NewSlot | MethodAttributes.Virtual | MethodAttributes.Final;
            }
            var attributes = MethodAttributes.HideBySig | MethodAttributes.Virtual | MethodAttributes.Final;
            if (methodInfo.IsPublic)
            {
                return MethodAttributes.Public | attributes;
            }
            if (methodInfo.IsFamily)
            {
                return MethodAttributes.Family | attributes;
            }
            if (methodInfo.IsFamilyAndAssembly)
            {
                return MethodAttributes.FamANDAssem | attributes;
            }
            if (methodInfo.IsFamilyOrAssembly)
            {
                return MethodAttributes.FamORAssem | attributes;
            }

            return null;
        }

        private void DefineMethodGenericParameters(MethodBuilder methodBuilder, MethodInfo method)
        {
           var genericParameters = method.GetGenericArguments();
           var genericParameterNames = Enumerable.Range(1, genericParameters.Length).Select(it => $"T{it}").ToArray();
           var builders = methodBuilder.DefineGenericParameters(genericParameterNames);
            for (int index = 0; index < genericParameters.Length; index++)
            {
                var builder = builders[index];
                var genericParameter = genericParameters[index];
                if (!genericParameter.IsGenericParameter)
                {
                    continue;
                }
                builder.SetGenericParameterAttributes(genericParameter.GenericParameterAttributes);

                var interfaceConstraints = new List<Type>();
                foreach (Type constraint in genericParameter.GetGenericParameterConstraints())
                {
                    if (constraint.IsClass)
                    {
                        builder.SetBaseTypeConstraint(constraint);
                    }
                    else
                    {
                        interfaceConstraints.Add(constraint);
                    }
                }
                if (interfaceConstraints.Count > 0)
                {
                    builder.SetInterfaceConstraints(interfaceConstraints.ToArray());
                }
            }  
        }

        private Type DefineTargetInvoker(MethodInfo methodInfo)
        {
            var className = $"TargetInvoker_{methodInfo}{GenerateSurfix()}";
            var typeBuilder = this.ModuleBuilder.DefineType(className, TypeAttributes.Public| TypeAttributes.Sealed);
            var genericParameterTypeMap = methodInfo.IsGenericMethod
               ? this.DefineTypeGenericParameters(typeBuilder, methodInfo)
               : new Dictionary<Type, Type>();                                                       
            var targetField = typeBuilder.DefineField("_target", methodInfo.DeclaringType, FieldAttributes.Private);
            var constructorBuilder = typeBuilder.DefineConstructor(MethodAttributes.Public, CallingConventions.HasThis, new Type[] { methodInfo.DeclaringType });
            var il = constructorBuilder.GetILGenerator();

            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Call, ReflectionUtility.ConstructorOfObject);

            il.Emit(OpCodes.Ldarg_0);   
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Stfld, targetField);
            il.Emit(OpCodes.Ret);

            var methodBuilder = typeBuilder.DefineMethod("Invoke", MethodAttributes.Public, typeof(Task), new Type[] { typeof(InvocationContext) });
            var parameters = methodInfo.GetParameters();
            var parameterTypes = parameters.Select(it => genericParameterTypeMap.TryGetValue(it.ParameterType, out var type) ? type.GetNonByRefType() : it.ParameterType.GetNonByRefType()).ToArray();

            il = methodBuilder.GetILGenerator();

            //InvocationContext.Arguments
            il.DeclareLocal(typeof(object[]));
            var returnType = methodInfo.ReturnType;
            if (methodInfo.ReturnType != typeof(void))
            {
                returnType = genericParameterTypeMap.TryGetValue(methodInfo.ReturnType, out var type)
                    ? type
                    : methodInfo.ReturnType;
                il.DeclareLocal(returnType);
            }

            var arguments = parameterTypes.Select(it => il.DeclareLocal(it)).ToArray();

            //Load and store InvocationContext.Arguments. 
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Callvirt, ReflectionUtility.ArgumentsPropertyOfInvocationContext.GetMethod);
            il.Emit(OpCodes.Stloc_0);

            //Load and store all arguments
            for (int index = 0; index < parameters.Length; index++)
            {
                var parameter = parameters[index];
                il.Emit(OpCodes.Ldloc_0);
                il.EmitLoadConstantInt32(index);
                il.Emit(OpCodes.Ldelem_Ref);
                il.EmitUnboxOrCast(parameterTypes[index]);
                il.Emit(OpCodes.Stloc, arguments[index]);
            }  

            //Invoke target method.
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldfld, targetField);
            for (int index = 0; index < parameters.Length; index++)
            {
                var parameter = parameters[index];
                if (parameter.ParameterType.IsByRef)
                {
                    il.Emit(OpCodes.Ldloca, arguments[index]);
                }
                else
                {
                    il.Emit(OpCodes.Ldloc, arguments[index]);
                }
            }
            if (methodInfo.IsGenericMethod)
            {
                var genericMethod = methodInfo.MakeGenericMethod(genericParameterTypeMap.Values.ToArray());
                if (this.TypeToIntercept.IsInterface)
                {
                    il.Emit(OpCodes.Callvirt, genericMethod);
                }
                else
                {
                    il.Emit(OpCodes.Call, genericMethod);
                }
            }
            else
            {
                if (this.TypeToIntercept.IsInterface)
                {
                    il.Emit(OpCodes.Callvirt, methodInfo);
                }
                else
                {
                    il.Emit(OpCodes.Call, methodInfo);
                }
            }

            //Save return value to InvocationContext.ReturnValue
            if (returnType != typeof(void))
            {
                il.Emit(OpCodes.Stloc_1);
                il.Emit(OpCodes.Ldarg_1);
                il.Emit(OpCodes.Ldloc_1);
                il.EmitBox(returnType);
                il.Emit(OpCodes.Callvirt, ReflectionUtility.ReturnValueOfInvocationContext.SetMethod);
            }

            //Set ref arguments InvocationContext.Arguments
            if (parameters.Any(it => it.ParameterType.IsByRef))
            {
                for (int index = 0; index < parameters.Length; index++)
                {
                    var parameter = parameters[index];
                    if (parameter.ParameterType.IsByRef)
                    {
                        il.Emit(OpCodes.Ldloc_0);
                        il.EmitLoadConstantInt32(index);
                        il.Emit(OpCodes.Ldloc, arguments[index]);
                        il.EmitBox(parameterTypes[index]);
                        il.Emit(OpCodes.Stelem_Ref);
                    }
                }
            }

            //return Task.CompletedTask
            il.Emit(OpCodes.Call, ReflectionUtility.CompletedTaskOfTask.GetMethod);
            il.Emit(OpCodes.Ret);

            return typeBuilder.CreateTypeInfo();
        }

        private Dictionary<Type, Type> DefineTypeGenericParameters(TypeBuilder typeBuilder, MethodInfo method)
        {
            var map = new Dictionary<Type, Type>();
            var genericParameters = method.GetGenericArguments();
            var genericParameterNames = Enumerable.Range(1, genericParameters.Length).Select(it => $"T{it}").ToArray();
            var builders = typeBuilder.DefineGenericParameters(genericParameterNames);
            for (int index = 0; index < genericParameters.Length; index++)
            {
                var builder = builders[index];
                var genericParameter = genericParameters[index];
                if (!genericParameter.IsGenericParameter)
                {
                    continue;
                }
                builder.SetGenericParameterAttributes(genericParameter.GenericParameterAttributes);

                var interfaceConstraints = new List<Type>();
                foreach (Type constraint in genericParameter.GetGenericParameterConstraints())
                {
                    if (constraint.IsClass)
                    {
                        builder.SetBaseTypeConstraint(constraint);
                    }
                    else
                    {
                        interfaceConstraints.Add(constraint);
                    }
                }
                if (interfaceConstraints.Count > 0)
                {
                    builder.SetInterfaceConstraints(interfaceConstraints.ToArray());
                } 
            }

            var genericParameters2 = typeBuilder.GetGenericArguments();
            for (int index = 0; index < genericParameters.Length; index++)
            {
                map.Add(genericParameters[index], genericParameters2[index]);
            }

            return map;
        }

        private static string GenerateSurfix()
        {
            return Guid.NewGuid().ToString().Replace("-", "");
        }
    }
}