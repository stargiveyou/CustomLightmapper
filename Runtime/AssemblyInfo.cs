using System.Runtime.CompilerServices;

// 어셈블리 분할(Runtime/Editor/Tests/Samples) 이전에는 모두 한 어셈블리라 internal 멤버
// (예: BVH.RayAABB, *RO 읽기전용 접근자)를 데모/테스트/에디터 코드가 그대로 사용했다.
// 분할 후에도 그 접근성을 유지하기 위해 형제 어셈블리에 friend 권한을 부여한다.
[assembly: InternalsVisibleTo("HuskyLibs.CustomLightmapper.Editor")]
[assembly: InternalsVisibleTo("HuskyLibs.CustomLightmapper.Tests")]
[assembly: InternalsVisibleTo("HuskyLibs.CustomLightmapper.Samples")]
